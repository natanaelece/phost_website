using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using Npgsql;
using Dapper;
using System;
using System.Linq;
using Microsoft.AspNetCore.RateLimiting;
using PremierAPI.Services;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/checkout")]
    [EnableRateLimiting("AuthLimiter")]
    public class CheckoutController : ControllerBase
    {
        private readonly AsaasApiClient _asaas;
        private readonly ILogger<CheckoutController> _logger;
        private readonly bool _debugLogs;
        private readonly string _connString;
        private readonly AdminNotificationEmailService _adminNotifications;
        private readonly MetaBusinessEventService _metaBusinessEvents;

        private sealed class ReferralDiscountData
        {
            public Guid? ReferredBy { get; set; }
            public bool UsedReferralDiscount { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private sealed class CouponDiscountData
        {
            public string Code { get; set; } = "";
            public string DiscountType { get; set; } = "";
            public decimal DiscountValue { get; set; }
        }

        public CheckoutController(
            ILogger<CheckoutController> logger,
            IConfiguration config,
            AsaasApiClient asaas,
            AdminNotificationEmailService adminNotifications,
            MetaBusinessEventService metaBusinessEvents)
        {
            _logger = logger;
            _asaas = asaas;
            _adminNotifications = adminNotifications;
            _metaBusinessEvents = metaBusinessEvents;
            _debugLogs = config.GetValue<bool>("PremierConfig:EnableDebugLogs");
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
        }

        private async Task<bool> ValidateSession(NpgsqlConnection db, Guid userId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return false;

            int valid = await db.QueryFirstOrDefaultAsync<int>(
                @"SELECT 1
                  FROM user_sessions s
                  INNER JOIN users u ON u.id = s.user_id
                  WHERE s.token = @Token
                    AND s.user_id = @UserId
                    AND s.expires_at > @Now
                    AND u.is_active = true",
                new { Token = token, UserId = userId, Now = DateTime.UtcNow });

            return valid == 1;
        }

        [HttpGet("pricing-rules")]
        public IActionResult GetPricingRules()
        {
            return Ok(new
            {
                minComputers = PricingRules.MinComputers,
                maxComputers = PricingRules.MaxComputers,
                minSlots = PricingRules.MinSlots,
                maxSlots = PricingRules.MaxSlots,
                minDailyComputers = PricingRules.MinDailyComputers,
                minDailyDays = PricingRules.MinDailyDays,
                maxDailyDays = PricingRules.MaxDailyDays,
                weeklyDays = PricingRules.WeeklyDays,
                monthlyDays = PricingRules.MonthlyDays,
                weeklyBasePrice = PricingRules.WeeklyBasePrice,
                dailyWeeklyBasePrice = PricingRules.DailyWeeklyBasePrice,
                additionalSlotPrice = PricingRules.AdditionalSlotPrice,
                additionalComputerDiscount = PricingRules.AdditionalComputerDiscount,
                monthlyWeeks = PricingRules.MonthlyWeeks,
                monthlyDiscountRate = PricingRules.MonthlyDiscountRate,
                referralDiscountRate = PricingRules.ReferralDiscountRate,
                commercialRoundingThreshold = PricingRules.CommercialRoundingThreshold,
                minimumPrices = new
                {
                    diaria = PricingRules.Calculate("diaria", PricingRules.MinDailyComputers, PricingRules.MinSlots, PricingRules.MinDailyDays).Total,
                    semanal = PricingRules.Calculate("semanal", PricingRules.MinComputers, PricingRules.MinSlots, PricingRules.WeeklyDays).Total,
                    mensal = PricingRules.Calculate("mensal", PricingRules.MinComputers, PricingRules.MinSlots, PricingRules.MonthlyDays).Total
                }
            });
        }

        [HttpPost("pricing-quote")]
        public IActionResult GetPricingQuote([FromBody] PricingQuoteRequest request)
        {
            try { return Ok(PricingRules.Calculate(request.Period, request.Computers, request.Slots, request.Days)); }
            catch (ArgumentException ex) { return BadRequest(new { erro = ex.Message }); }
        }

        [HttpPost("gerarpix")]
        public async Task<IActionResult> GerarPix([FromBody] PedidoRequest pedido)
        {
            if (_debugLogs) _logger.LogInformation("[CHECKOUT] Geração de Pix iniciada.");

            using var db = new NpgsqlConnection(_connString);
            await db.OpenAsync();
            
            // 1. Validar Sessão
            if (!await ValidateSession(db, pedido.UserId)) 
                return Unauthorized(new { erro = "Sessão expirada ou inválida." });

            PricingQuote baseQuote;
            try { baseQuote = PricingRules.Calculate(pedido.Periodo, pedido.Pcs, pedido.Wyds, pedido.Dias); }
            catch (ArgumentException ex) { return BadRequest(new { erro = ex.Message }); }
            pedido.Pcs = baseQuote.Computers;
            pedido.Wyds = baseQuote.Slots;
            pedido.Dias = baseQuote.Days;
            pedido.Periodo = baseQuote.Period;

            if (!Regex.IsMatch(pedido.AnydeskId ?? "", @"^\d{6,15}$"))
                return BadRequest(new { erro = "O ID do AnyDesk deve conter de 6 a 15 números." });

            pedido.WydServerName = (pedido.WydServerName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pedido.WydServerName) || pedido.WydServerName.Length > 50)
                return BadRequest(new { erro = "Informe o nome do servidor de WYD com até 50 caracteres.", campo = "wydServerName" });
            if (IsUnsupportedWydServer(pedido.WydServerName))
                return BadRequest(new { erro = "No momento, não atendemos o servidor WYD2.", campo = "wydServerName", codigo = "unsupported_wyd_server" });

            // 3. Recalcular Preço no Backend
            decimal bruto = baseQuote.Gross;
            decimal descPeriodo = baseQuote.PeriodDiscount;
            decimal descHardware = baseQuote.HardwareDiscount;
            decimal descIndicacao = 0;
            decimal descCupom = 0;
            CouponDiscountData? coupon = null;
            if (pedido.UsouDescontoIndicacao && bruto > 0)
            {
                // Verifica se usuário realmente tem direito ao desconto
                var user = await db.QueryFirstOrDefaultAsync<ReferralDiscountData>(
                    @"SELECT referred_by AS ReferredBy,
                             used_referral_discount AS UsedReferralDiscount,
                             created_at AS CreatedAt
                      FROM users
                      WHERE id = @Id",
                    new { Id = pedido.UserId });

                if (user?.ReferredBy != null && !user.UsedReferralDiscount)
                {
                    if (user.CreatedAt.AddHours(12) > DateTime.Now)
                    {
                        descIndicacao = bruto * PricingRules.ReferralDiscountRate;
                    }
                    else
                    {
                        pedido.UsouDescontoIndicacao = false; // Expirou
                    }
                }
                else
                {
                    pedido.UsouDescontoIndicacao = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(pedido.CodigoCupom) && bruto > 0)
            {
                coupon = await db.QueryFirstOrDefaultAsync<CouponDiscountData>(
                    @"SELECT code AS Code,
                             discount_type AS DiscountType,
                             discount_value AS DiscountValue
                      FROM coupons
                      WHERE code = @Code
                        AND is_active = true
                        AND (max_uses IS NULL OR COALESCE(uses, 0) < max_uses)",
                    new { Code = pedido.CodigoCupom.Trim().ToUpperInvariant() });

                if (coupon == null)
                    return BadRequest(new { erro = "Cupom inválido ou esgotado." });

                descCupom = coupon.DiscountType == "percent"
                    ? bruto * (coupon.DiscountValue / 100M)
                    : coupon.DiscountValue;
            }

            pedido.Total = PricingRules.ApplyCommercialRounding(bruto - descPeriodo - descHardware - descIndicacao - descCupom);

            await using var transaction = await db.BeginTransactionAsync();
            int lockedUser = await db.QueryFirstOrDefaultAsync<int>(
                "SELECT 1 FROM users WHERE id = @UserId FOR UPDATE",
                new { UserId = pedido.UserId },
                transaction);
            if (lockedUser != 1)
                return Unauthorized(new { erro = "Usuário não encontrado." });

            Guid? pendingOrderId = await db.QueryFirstOrDefaultAsync<Guid?>(
                @"SELECT id
                  FROM orders
                  WHERE user_id = @UserId AND status = 'pendente'
                  ORDER BY created_at DESC
                  LIMIT 1",
                new { UserId = pedido.UserId },
                transaction);
            if (pendingOrderId.HasValue)
                return Conflict(new { erro = "Você já possui um pedido pendente. Pague ou cancele esse pedido antes de gerar outro PIX." });

            string pixAddressKey = await GetActivePixAddressKeyAsync(HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(pixAddressKey))
                return BadRequest(new { erro = "Nenhuma chave Pix ativa foi encontrada na conta Asaas." });

            Guid orderId = Guid.NewGuid();
            Guid? metaAttributionId = await CreateMetaAttributionSnapshotAsync(
                db,
                pedido.UserId,
                transaction);
            const int pixExpirationSeconds = 900;
            DateTime pixExpiresAt = DateTime.Now.AddSeconds(pixExpirationSeconds);
            var asaasPayload = new AsaasStaticPixRequest(
                pixAddressKey,
                $"Licença ({pedido.Periodo}) - AnyDesk: {pedido.AnydeskId}",
                pedido.Total,
                "ALL",
                pixExpirationSeconds,
                false,
                orderId.ToString());
            AsaasApiOperationResult<AsaasStaticPixQrCode> createResult =
                await _asaas.CreateStaticPixQrCodeAsync(
                    asaasPayload,
                    cancellationToken: HttpContext.RequestAborted);
            if (!createResult.IsSuccess || createResult.Value == null)
            {
                if (createResult.IsInvalidResponse)
                    return BadRequest(new { erro = "O gateway retornou um QR Code incompleto." });
                return BadRequest(new { erro = "O Asaas não conseguiu gerar o QR Code Pix. Verifique se a chave Pix da conta está ativa." });
            }
            string qrCodeId = createResult.Value.Id;
            string encodedImage = createResult.Value.EncodedImage;
            string pixPayload = createResult.Value.Payload;

            try
            {
                string sqlOrder = @"INSERT INTO orders
                    (id, user_id, anydesk_id, wyd_server_name, computers, wyds_per_computer, period, days, total_price,
                     asaas_pix_qr_code_id, pix_payload, pix_encoded_image, pix_expires_at, status, meta_attribution_id)
                    VALUES
                    (@Id, @UserId, @Anydesk, @WydServerName, @Comps, @Wyds, @Period, @Days, @Total,
                     @QrCodeId, @PixPayload, @EncodedImage, @PixExpiresAt, 'pendente', @MetaAttributionId)";
                await db.ExecuteAsync(sqlOrder, new {
                    Id = orderId,
                    UserId = pedido.UserId,
                    Anydesk = pedido.AnydeskId,
                    WydServerName = pedido.WydServerName,
                    Comps = pedido.Pcs,
                    Wyds = pedido.Wyds,
                    Period = pedido.Periodo,
                    Days = pedido.Dias,
                    Total = pedido.Total,
                    QrCodeId = qrCodeId,
                    PixPayload = pixPayload,
                    EncodedImage = encodedImage,
                    PixExpiresAt = pixExpiresAt,
                    MetaAttributionId = metaAttributionId
                }, transaction);

                if (pedido.UsouDescontoIndicacao)
                {
                    await db.ExecuteAsync(
                        "UPDATE users SET used_referral_discount = true WHERE id = @UserId",
                        new { UserId = pedido.UserId }, transaction);
                }

                if (coupon != null)
                {
                    int couponRows = await db.ExecuteAsync(
                        @"UPDATE coupons
                          SET uses = COALESCE(uses, 0) + 1
                          WHERE code = @Code
                            AND is_active = true
                            AND (max_uses IS NULL OR COALESCE(uses, 0) < max_uses)",
                        new { coupon.Code }, transaction);
                    if (couponRows != 1)
                        throw new InvalidOperationException("O cupom ficou indisponível durante a geração do Pix.");
                }

                await transaction.CommitAsync();
            }
            catch
            {
                _ = await _asaas.CancelStaticPixQrCodeAsync(
                    qrCodeId,
                    acceptNotFound: true,
                    cancellationToken: CancellationToken.None);
                throw;
            }

            await _adminNotifications.TrySendOrderCreatedAsync(orderId);
            if (MetaBusinessEventPolicy.ShouldSendInitiateCheckout(
                orderPersisted: true,
                pixGenerated: !string.IsNullOrWhiteSpace(qrCodeId)
                    && !string.IsNullOrWhiteSpace(encodedImage)
                    && !string.IsNullOrWhiteSpace(pixPayload)))
            {
                await _metaBusinessEvents.TrySendInitiateCheckoutAsync(
                    orderId,
                    HttpContext.RequestAborted);
            }
            string metaEventId = MetaBusinessEventService.InitiateCheckoutEventId(orderId);
            var metaCustomData = MetaBusinessEventService.BuildCommerceCustomData(
                orderId,
                pedido.Periodo,
                pedido.Pcs,
                pedido.Total);

            return Ok(new
            {
                encodedImage,
                payload = pixPayload,
                paymentId = qrCodeId,
                total = pedido.Total,
                expiresAt = pixExpiresAt,
                expiresInSeconds = pixExpirationSeconds,
                metaEventId,
                metaEvent = new
                {
                    eventName = "InitiateCheckout",
                    eventId = metaEventId,
                    customData = metaCustomData
                }
            });
        }

        [HttpPost("manual/{orderId}/generate-pix")]
        public async Task<IActionResult> GeneratePixForManualOrder(Guid orderId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return Unauthorized(new { erro = "Sessão inválida." });

            using var db = new NpgsqlConnection(_connString);
            var order = await db.QueryFirstOrDefaultAsync(
                @"SELECT o.id, o.user_id, o.anydesk_id, o.wyd_server_name, o.computers,
                         o.wyds_per_computer, o.period, o.days, o.total_price, o.status,
                         o.created_manually, o.asaas_pix_qr_code_id
                  FROM orders o
                  INNER JOIN user_sessions s ON s.user_id = o.user_id
                  INNER JOIN users u ON u.id = o.user_id
                  WHERE o.id = @OrderId AND s.token = @Token AND s.expires_at > @Now
                    AND u.is_active = true",
                new { OrderId = orderId, Token = token, Now = DateTime.UtcNow });
            if (order == null) return Unauthorized(new { erro = "Pedido não encontrado para esta sessão." });
            if (!(order.created_manually as bool? ?? false)) return BadRequest(new { erro = "Este não é um pedido criado pela equipe." });
            if ((string)order.status != "pendente") return BadRequest(new { erro = "Este pedido não está mais pendente." });
            if (order.asaas_pix_qr_code_id != null) return Conflict(new { erro = "O PIX deste pedido já foi gerado." });

            string anydesk = (order.anydesk_id as string ?? "").Trim();
            string server = (order.wyd_server_name as string ?? "").Trim();
            string period = ((string)order.period).Trim().ToLowerInvariant();
            int computers = (int)order.computers;
            int wyds = (int)order.wyds_per_computer;
            int days = (int)order.days;
            decimal total = (decimal)order.total_price;
            if (!Regex.IsMatch(anydesk, @"^\d{6,15}$")) return BadRequest(new { erro = "O pedido possui um ID do AnyDesk inválido. Fale com o suporte." });
            if (string.IsNullOrWhiteSpace(server) || server.Length > 50 || IsUnsupportedWydServer(server)) return BadRequest(new { erro = "O servidor WYD do pedido precisa ser corrigido pelo suporte." });
            if (period == "personalizado")
            {
                if (days < 1 || days > 3650 ||
                    computers < PricingRules.MinComputers || computers > PricingRules.MaxComputers ||
                    wyds < PricingRules.MinSlots || wyds > PricingRules.MaxSlots)
                    return BadRequest(new { erro = "A configuração do pedido personalizado precisa ser corrigida pelo suporte." });
            }
            else
            {
                try { PricingRules.Calculate(period, computers, wyds, days); }
                catch (ArgumentException ex) { return BadRequest(new { erro = $"A configuração do pedido precisa ser corrigida pelo suporte: {ex.Message}" }); }
            }
            if (total <= 0) return BadRequest(new { erro = "O valor do pedido precisa ser corrigido pelo suporte." });

            string pixAddressKey = await GetActivePixAddressKeyAsync(HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(pixAddressKey)) return BadRequest(new { erro = "Nenhuma chave Pix ativa foi encontrada na conta Asaas." });

            const int pixExpirationSeconds = 900;
            DateTime pixExpiresAt = DateTime.Now.AddSeconds(pixExpirationSeconds);
            var asaasPayload = new AsaasStaticPixRequest(
                pixAddressKey,
                $"Licença ({period}) - AnyDesk: {anydesk}",
                total,
                "ALL",
                pixExpirationSeconds,
                false,
                orderId.ToString());
            AsaasApiOperationResult<AsaasStaticPixQrCode> createResult =
                await _asaas.CreateStaticPixQrCodeAsync(
                    asaasPayload,
                    cancellationToken: HttpContext.RequestAborted);
            if (!createResult.IsSuccess || createResult.Value == null)
            {
                if (createResult.IsInvalidResponse)
                    return BadRequest(new { erro = "O gateway retornou um QR Code incompleto." });
                return BadRequest(new { erro = "O Asaas não conseguiu gerar o QR Code Pix agora." });
            }
            string qrCodeId = createResult.Value.Id;
            string encodedImage = createResult.Value.EncodedImage;
            string pixPayload = createResult.Value.Payload;

            Guid? metaAttributionId = await CreateMetaAttributionSnapshotAsync(
                db,
                (Guid)order.user_id);
            int updated = await db.ExecuteAsync(
                @"UPDATE orders SET asaas_pix_qr_code_id = @QrCodeId, pix_payload = @PixPayload,
                                    pix_encoded_image = @EncodedImage, pix_expires_at = @PixExpiresAt,
                                    meta_attribution_id = COALESCE(@MetaAttributionId, meta_attribution_id)
                  WHERE id = @OrderId AND status = 'pendente' AND created_manually = true
                    AND asaas_pix_qr_code_id IS NULL",
                new
                {
                    QrCodeId = qrCodeId,
                    PixPayload = pixPayload,
                    EncodedImage = encodedImage,
                    PixExpiresAt = pixExpiresAt,
                    OrderId = orderId,
                    MetaAttributionId = metaAttributionId
                });
            if (updated != 1)
            {
                _ = await _asaas.CancelStaticPixQrCodeAsync(
                    qrCodeId,
                    acceptNotFound: true,
                    cancellationToken: CancellationToken.None);
                return Conflict(new { erro = "O estado deste pedido mudou. Atualize a página." });
            }

            if (MetaBusinessEventPolicy.ShouldSendInitiateCheckout(
                orderPersisted: updated == 1,
                pixGenerated: !string.IsNullOrWhiteSpace(qrCodeId)
                    && !string.IsNullOrWhiteSpace(encodedImage)
                    && !string.IsNullOrWhiteSpace(pixPayload)))
            {
                await _metaBusinessEvents.TrySendInitiateCheckoutAsync(
                    orderId,
                    HttpContext.RequestAborted);
            }
            string metaEventId = MetaBusinessEventService.InitiateCheckoutEventId(orderId);
            return Ok(new
            {
                encodedImage,
                payload = pixPayload,
                paymentId = qrCodeId,
                total,
                expiresAt = pixExpiresAt,
                expiresInSeconds = pixExpirationSeconds,
                metaEventId,
                metaEvent = new
                {
                    eventName = "InitiateCheckout",
                    eventId = metaEventId,
                    customData = MetaBusinessEventService.BuildCommerceCustomData(
                        orderId,
                        period,
                        computers,
                        total)
                }
            });
        }

        [HttpPost("manual/{orderId}/cancel")]
        public async Task<IActionResult> CancelManualOrderDraft(Guid orderId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return Unauthorized();
            using var db = new NpgsqlConnection(_connString);
            int updated = await db.ExecuteAsync(
                @"UPDATE orders o SET status = 'cancelado', canceled_at = CURRENT_TIMESTAMP
                  FROM user_sessions s
                  WHERE o.id = @OrderId AND o.user_id = s.user_id AND s.token = @Token
                    AND s.expires_at > @Now AND o.status = 'pendente' AND o.created_manually = true
                    AND o.asaas_pix_qr_code_id IS NULL AND o.asaas_payment_id IS NULL",
                new { OrderId = orderId, Token = token, Now = DateTime.UtcNow });
            if (updated != 1) return BadRequest(new { erro = "Este pedido não pode mais ser cancelado por este fluxo." });
            await _adminNotifications.TrySendOrderCanceledAsync(orderId);
            return Ok(new { success = true });
        }

        [HttpPost("validate-server")]
        public IActionResult ValidateWydServer([FromBody] ValidateWydServerRequest request)
        {
            string serverName = (request.ServerName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(serverName) || serverName.Length > 50)
                return BadRequest(new { erro = "Informe o nome do servidor de WYD com até 50 caracteres.", campo = "wydServerName" });
            if (IsUnsupportedWydServer(serverName))
                return BadRequest(new { erro = "No momento, não atendemos o servidor WYD2.", campo = "wydServerName", codigo = "unsupported_wyd_server" });
            return Ok(new { valido = true });
        }

        private static bool IsUnsupportedWydServer(string serverName)
        {
            string normalized = serverName.Trim();
            return normalized.Equals("wyd2", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("wyd 2", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("pending/{userId}")]
        public async Task<IActionResult> GetPendingPix(Guid userId)
        {
            using var db = new NpgsqlConnection(_connString);
            if (!await ValidateSession(db, userId)) return Unauthorized(new { erro = "Sessão inválida." });
            
            var pendingOrder = await db.QueryFirstOrDefaultAsync(
                @"SELECT id, asaas_payment_id, asaas_pix_qr_code_id, total_price, pix_payload,
                         pix_encoded_image, pix_expires_at, created_at, created_manually,
                         period, days, computers, wyds_per_computer
                  FROM orders 
                  WHERE user_id = @UserId AND status = 'pendente' 
                  ORDER BY created_at DESC LIMIT 1",
                new { UserId = userId });

            if (pendingOrder == null) return NotFound();

            string? staticQrCodeId = pendingOrder.asaas_pix_qr_code_id as string;
            bool createdManually = pendingOrder.created_manually as bool? ?? false;
            bool manualWithoutPix = createdManually
                && staticQrCodeId == null && pendingOrder.asaas_payment_id == null;
            if (manualWithoutPix)
            {
                return Ok(new
                {
                    orderId = (Guid)pendingOrder.id,
                    total = (decimal)pendingOrder.total_price,
                    requiresPixGeneration = true,
                    period = (string)pendingOrder.period,
                    days = (int)pendingOrder.days,
                    computers = (int)pendingOrder.computers,
                    wydsPerComputer = (int)pendingOrder.wyds_per_computer
                });
            }
            DateTime expiresAt = pendingOrder.pix_expires_at as DateTime?
                ?? ((DateTime)pendingOrder.created_at).AddMinutes(15);
            if (expiresAt <= DateTime.Now)
            {
                string expiredId = staticQrCodeId ?? (string)pendingOrder.asaas_payment_id;
                if (staticQrCodeId != null)
                {
                    _ = await _asaas.CancelStaticPixQrCodeAsync(
                        staticQrCodeId,
                        acceptNotFound: true,
                        cancellationToken: HttpContext.RequestAborted);
                }
                else
                {
                    _ = await _asaas.CancelPaymentAsync(
                        expiredId,
                        acceptNotFound: true,
                        cancellationToken: HttpContext.RequestAborted);
                }
                if (createdManually)
                {
                    await db.ExecuteAsync(
                        @"UPDATE orders SET asaas_payment_id = NULL, asaas_pix_qr_code_id = NULL,
                                            pix_payload = NULL, pix_encoded_image = NULL, pix_expires_at = NULL
                          WHERE id = @OrderId AND status = 'pendente'",
                        new { OrderId = (Guid)pendingOrder.id });
                    return Ok(new
                    {
                        orderId = (Guid)pendingOrder.id,
                        total = (decimal)pendingOrder.total_price,
                        requiresPixGeneration = true,
                        period = (string)pendingOrder.period,
                        days = (int)pendingOrder.days,
                        computers = (int)pendingOrder.computers,
                        wydsPerComputer = (int)pendingOrder.wyds_per_computer
                    });
                }
                await db.ExecuteAsync(
                    @"UPDATE orders SET status = 'expirado', pix_payload = NULL, pix_encoded_image = NULL
                      WHERE (asaas_pix_qr_code_id = @Id OR asaas_payment_id = @Id) AND status = 'pendente'",
                    new { Id = expiredId });
                return NotFound();
            }

            if (staticQrCodeId == null)
            {
                string legacyPaymentId = (string)pendingOrder.asaas_payment_id;
                AsaasApiOperationResult<AsaasPixQrCode> qrCodeResult =
                    await _asaas.GetPaymentPixQrCodeAsync(
                        legacyPaymentId,
                        cancellationToken: HttpContext.RequestAborted);
                if (!qrCodeResult.IsSuccess || qrCodeResult.Value == null) return NotFound();
                return Ok(new
                {
                    paymentId = legacyPaymentId,
                    total = pendingOrder.total_price,
                    encodedImage = qrCodeResult.Value.EncodedImage,
                    payload = qrCodeResult.Value.Payload,
                    expiresAt,
                    expiresInSeconds = Math.Max(0, (int)(expiresAt - DateTime.Now).TotalSeconds)
                });
            }

            return Ok(new
            {
                paymentId = staticQrCodeId,
                total = pendingOrder.total_price,
                encodedImage = pendingOrder.pix_encoded_image,
                payload = pendingOrder.pix_payload,
                expiresAt = expiresAt,
                expiresInSeconds = Math.Max(0, (int)(expiresAt - DateTime.Now).TotalSeconds)
            });
        }

        [HttpPost("cancel/{paymentId}")]
        public async Task<IActionResult> CancelPix(string paymentId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return Unauthorized();

            using var db = new NpgsqlConnection(_connString);
            
            // Ensure the payment belongs to a valid session
            var order = await db.QueryFirstOrDefaultAsync(
                @"SELECT o.id AS order_id, o.user_id, o.asaas_pix_qr_code_id, o.status,
                         o.pix_expires_at, o.created_at
                  FROM orders o
                  INNER JOIN user_sessions s ON s.user_id = o.user_id
                  WHERE (o.asaas_pix_qr_code_id = @Id OR o.asaas_payment_id = @Id)
                    AND s.token = @Token AND s.expires_at > @Now",
                new { Id = paymentId, Token = token, Now = DateTime.UtcNow });
                
            if (order == null) return Unauthorized(new { erro = "Não autorizado." });
            if ((string)order.status != "pendente")
                return BadRequest(new { erro = "Este Pix não está mais pendente." });

            string? qrCodeId = order.asaas_pix_qr_code_id as string;
            DateTime orderExpiresAt = order.pix_expires_at as DateTime?
                ?? ((DateTime)order.created_at).AddMinutes(15);
            bool expired = orderExpiresAt <= DateTime.Now;
            AsaasApiOperationResult deleteResult = !string.IsNullOrWhiteSpace(qrCodeId)
                ? await _asaas.CancelStaticPixQrCodeAsync(
                    qrCodeId,
                    acceptNotFound: true,
                    cancellationToken: HttpContext.RequestAborted)
                : await _asaas.CancelPaymentAsync(
                    paymentId,
                    acceptNotFound: true,
                    cancellationToken: HttpContext.RequestAborted);
            if (!deleteResult.IsSuccess)
                return BadRequest(new { erro = "Não foi possível cancelar o Pix no Asaas." });

            int updated = await db.ExecuteAsync(
                @"UPDATE orders
                  SET status = @Status,
                      canceled_at = CASE WHEN @Status = 'cancelado' THEN CURRENT_TIMESTAMP ELSE NULL END,
                      pix_payload = NULL, pix_encoded_image = NULL
                  WHERE (asaas_pix_qr_code_id = @Id OR asaas_payment_id = @Id) AND status = 'pendente'",
                new { Id = paymentId, Status = expired ? "expirado" : "cancelado" });

            if (updated == 1 && !expired)
                await _adminNotifications.TrySendOrderCanceledAsync((Guid)order.order_id);
            
            return Ok(new { success = true });
        }

        private async Task<string> GetActivePixAddressKeyAsync(
            CancellationToken cancellationToken)
        {
            AsaasApiOperationResult<string> result =
                await _asaas.GetActivePixAddressKeyAsync(
                    cancellationToken: cancellationToken);
            return result.IsSuccess ? result.Value ?? string.Empty : string.Empty;
        }

        private async Task<Guid?> CreateMetaAttributionSnapshotAsync(
            NpgsqlConnection db,
            Guid userId,
            NpgsqlTransaction? transaction = null)
        {
            Guid? requestedId = MetaAttributionService.ParseAttributionId(
                Request.Headers["X-Meta-Attribution-Id"].FirstOrDefault());
            if (!requestedId.HasValue) return null;

            const string savepoint = "meta_attribution_snapshot";
            try
            {
                if (transaction != null)
                    await db.ExecuteAsync($"SAVEPOINT {savepoint}", transaction: transaction);

                Guid? snapshotId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                    INSERT INTO meta_attributions
                        (id, user_id, source_attribution_id,
                         consent_status, consent_version, consented_at,
                         fbp, fbc, fbclid, client_ip_address, client_user_agent,
                         source_url, captured_at, updated_at)
                    SELECT
                        gen_random_uuid(), @UserId, COALESCE(source_attribution_id, id),
                        consent_status, consent_version,
                        consented_at, fbp, fbc, fbclid, client_ip_address,
                        client_user_agent, source_url, CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP
                    FROM meta_attributions
                    WHERE id = @Id
                      AND consent_status = 'accepted'
                    RETURNING id;",
                    new { Id = requestedId.Value, UserId = userId },
                    transaction);

                if (transaction != null)
                    await db.ExecuteAsync($"RELEASE SAVEPOINT {savepoint}", transaction: transaction);
                return snapshotId;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    await db.ExecuteAsync($"ROLLBACK TO SAVEPOINT {savepoint}", transaction: transaction);
                    await db.ExecuteAsync($"RELEASE SAVEPOINT {savepoint}", transaction: transaction);
                }
                _logger.LogError(
                    ex,
                    "[META ATRIBUICAO] Falha ao criar snapshot de atribuição do usuário {UserId}; checkout continuará sem marketing.",
                    userId);
                return null;
            }
        }
        
        [HttpGet("status/{paymentId}")]
        public async Task<IActionResult> CheckStatus(string paymentId)
        {
            using var db = new NpgsqlConnection(_connString);
            string? status = await db.QueryFirstOrDefaultAsync<string>(
                "SELECT status FROM orders WHERE asaas_payment_id = @Id OR asaas_pix_qr_code_id = @Id",
                new { Id = paymentId });
            // O campo known permite que outros webhooks da mesma conta Asaas reconheçam
            // com segurança os QR Codes da PremierHost sem depender da descrição da cobrança.
            // Mantemos "pendente" como fallback para não quebrar clientes antigos deste endpoint.
            return Ok(new { status = status ?? "pendente", known = status != null });
        }
        
        [HttpGet("cupom/{codigo}")]
        public async Task<IActionResult> ValidarCupom(string codigo)
        {
            using var db = new NpgsqlConnection(_connString);
            var cupom = await db.QueryFirstOrDefaultAsync(
                "SELECT code, discount_type, discount_value FROM coupons WHERE code = @Code AND is_active = true AND (max_uses IS NULL OR uses < max_uses)", 
                new { Code = codigo.ToUpper() });
            
            if (cupom == null) return BadRequest(new { erro = "Cupom inválido ou expirado." });
            return Ok(cupom);
        }
    }

    public class PedidoRequest 
    { 
        public Guid UserId { get; set; }
        public decimal Total { get; set; } 
        public string AnydeskId { get; set; } = ""; 
        public string WydServerName { get; set; } = "";
        public string Periodo { get; set; } = ""; 
        public string Nome { get; set; } = ""; 
        public string Whatsapp { get; set; } = ""; 
        public int Pcs { get; set; }
        public int Wyds { get; set; }
        public int Dias { get; set; }
        public bool UsouDescontoIndicacao { get; set; }
        public string CodigoCupom { get; set; } = "";
    }

    public class ValidateWydServerRequest
    {
        public string ServerName { get; set; } = "";
    }

    public class PricingQuoteRequest
    {
        public string Period { get; set; } = "";
        public int Computers { get; set; }
        public int Slots { get; set; }
        public int Days { get; set; }
    }
}
