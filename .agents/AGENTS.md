Você está trabalhando no projeto PremierAPI. ANTES de começar qualquer tarefa, alterar arquiteturas ou sugerir bibliotecas, você DEVE LER OBRIGATORIAMENTE o arquivo `README.md` para o contexto geral, e logo em seguida o arquivo `rules.md` na raiz do projeto. Eles documentam as regras rígidas sobre Dapper, Tailwind, Active Directory e frontend estático, além do simulador público, modais de ajuda e mídia local do frontend, e não podem ser ignorados.

## Contexto financeiro atual (leia antes de investigar o Asaas)

- O checkout usa `/v3/pix/qrCodes/static` para gerar Pix individual, de valor fixo, uso unico e 15 minutos. Essa foi uma decisao consciente para nao pedir CPF/CNPJ ao cliente; nao volte para cobranca dinamica sem autorizacao expressa.
- A descricao historica enviada ao QR e `Licença ({periodo}) - AnyDesk: {id}`. Contudo, no QR estatico o Asaas cria a cobranca apenas depois que recebe o Pix e pode usar uma descricao automatica na cobranca e no webhook.
- A identidade confiavel das compras atuais e `payment.pixQrCodeId` relacionado a `orders.asaas_pix_qr_code_id`. `description.StartsWith("Licença")` e apenas o fallback para cobrancas dinamicas antigas. Nao remova nenhum dos dois caminhos.
- Outra aplicacao recebe webhooks da mesma conta Asaas. Ela tambem precisa aceitar `pixQrCodeId`; nao e possivel garantir o prefixo da descricao na cobranca automatica sem abandonar o fluxo estatico, e editar depois do pagamento nao corrige o evento ja entregue.
- Depois do pagamento, o webhook local salva o cliente Asaas, sincroniza nome/e-mail/WhatsApp, define o grupo `PremierHost` e desativa todas as notificacoes. Nao faca chamadas reais nem altere clientes/cobrancas em producao sem pedido expresso do usuario.
