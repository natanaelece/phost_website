let userData = null;
        let eligibleForDiscount = false;
        let discountExpiresAt = null;
        let discountInterval = null;
        let pixPollingInterval = null;
        let cupomAtivo = null;
        let pendingPixData = null;
        let checkoutInFlight = false;
        let isLoggedIn = false;
        let pricingRulesPromise = null;

        function getPricingRules() {
            if (!pricingRulesPromise) pricingRulesPromise = fetch('/api/checkout/pricing-rules').then(response => {
                if (!response.ok) throw new Error('Não foi possível carregar as regras de preço.');
                return response.json();
            });
            return pricingRulesPromise;
        }

        function fecharMenuMobile() {
            document.getElementById('mobileMenu')?.classList.add('hidden');
            document.querySelector('[aria-controls="mobileMenu"]')?.setAttribute('aria-expanded', 'false');
        }

        function toggleMobileMenu() {
            const menu = document.getElementById('mobileMenu');
            const button = document.querySelector('[aria-controls="mobileMenu"]');
            const willOpen = menu.classList.contains('hidden');
            menu.classList.toggle('hidden');
            button?.setAttribute('aria-expanded', String(willOpen));
        }

        function toggleHistory(button) {
            const content = document.getElementById('historyContent');
            const willOpen = content.classList.contains('hidden');
            content.classList.toggle('hidden');
            document.getElementById('historyIcon').classList.toggle('rotate-180');
            button.setAttribute('aria-expanded', String(willOpen));
        }

        function toggleFreeTrial(button) {
            const content = document.getElementById('freeTrialContent');
            const willOpen = content.classList.contains('hidden');
            content.classList.toggle('hidden');
            document.getElementById('freeTrialIcon').classList.toggle('rotate-180');
            button.setAttribute('aria-expanded', String(willOpen));
        }

        function togglePasswordFields(button) {
            const content = document.getElementById('passwordChangeBlock');
            const willOpen = content.classList.contains('hidden');
            content.classList.toggle('hidden');
            button.setAttribute('aria-expanded', String(willOpen));
            if (willOpen) document.getElementById('profPassCurrent').focus();
        }

        function fecharPixModal() {
            document.getElementById('pixModal').classList.add('hidden');
            document.body.classList.remove('overflow-hidden');
        }

        function abrirAvisoServidor() {
            const modal = document.getElementById('wydServerModal');
            modal.classList.remove('hidden');
            document.body.classList.add('overflow-hidden');
            setTimeout(() => modal.querySelector('button')?.focus(), 0);
        }

        function fecharAvisoServidor() {
            document.getElementById('wydServerModal').classList.add('hidden');
            document.body.classList.remove('overflow-hidden');
            document.getElementById('wydServerName')?.focus();
        }

        function abrirAjuda(tipo) {
            const modal = document.getElementById('ajudaModal');
            const comoUsar = document.getElementById('ajudaComoUsar');
            const faq = document.getElementById('ajudaFaq');
            const titulo = document.getElementById('ajudaTitulo');
            const mostrarFaq = tipo === 'faq';

            comoUsar.classList.toggle('hidden', mostrarFaq);
            faq.classList.toggle('hidden', !mostrarFaq);
            titulo.innerText = mostrarFaq ? 'Dúvidas frequentes' : 'Como usar a Premier Host';
            modal.classList.remove('hidden');
            modal.classList.add('flex');
            document.body.classList.add('overflow-hidden');
        }

        function fecharAjuda() {
            const modal = document.getElementById('ajudaModal');
            modal.classList.add('hidden');
            modal.classList.remove('flex');
            document.body.classList.remove('overflow-hidden');
            modal.querySelectorAll('video').forEach(video => video.pause());
        }

        function fecharAjudaAoClicarFora(event) {
            if (event.target.id === 'ajudaModal') fecharAjuda();
        }

        function abrirBoasVindasCanal() {
            if (!userData?.id) return;
            const storageKey = `premier_channel_welcome_${userData.id}`;
            if (sessionStorage.getItem(storageKey) === 'shown') return;
            sessionStorage.setItem(storageKey, 'shown');
            const modal = document.getElementById('welcomeChannelModal');
            modal.classList.remove('hidden');
            modal.classList.add('flex');
            document.body.classList.add('overflow-hidden');
            setTimeout(() => modal.querySelector('a')?.focus(), 0);
        }

        function fecharBoasVindasCanal() {
            const modal = document.getElementById('welcomeChannelModal');
            modal.classList.add('hidden');
            modal.classList.remove('flex');
            document.body.classList.remove('overflow-hidden');
        }

        function openFreeTrialIneligibleModal() {
            const modal = document.getElementById('freeTrialIneligibleModal');
            modal.classList.remove('hidden');
            modal.classList.add('flex');
            document.body.classList.add('overflow-hidden');
            setTimeout(() => modal.querySelector('button')?.focus(), 0);
        }

        function closeFreeTrialIneligibleModal() {
            const modal = document.getElementById('freeTrialIneligibleModal');
            modal.classList.add('hidden');
            modal.classList.remove('flex');
            document.body.classList.remove('overflow-hidden');
        }

        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && !document.getElementById('ajudaModal')?.classList.contains('hidden')) {
                fecharAjuda();
            }
            if (event.key === 'Escape' && !document.getElementById('pixModal')?.classList.contains('hidden')) fecharPixModal();
            if (event.key === 'Escape' && !document.getElementById('wydServerModal')?.classList.contains('hidden')) fecharAvisoServidor();
            if (event.key === 'Escape' && !document.getElementById('profileModal')?.classList.contains('hidden')) fecharPerfil();
            if (event.key === 'Escape' && !document.getElementById('welcomeChannelModal')?.classList.contains('hidden')) fecharBoasVindasCanal();
            if (event.key === 'Escape' && !document.getElementById('freeTrialIneligibleModal')?.classList.contains('hidden')) closeFreeTrialIneligibleModal();
            if (event.key === 'Tab') {
                const modal = [...document.querySelectorAll('[role="dialog"]')].find(item => !item.classList.contains('hidden'));
                if (!modal) return;
                const focusable = [...modal.querySelectorAll('button:not([disabled]),a[href],input:not([disabled]),summary,video[controls]')].filter(el => !el.closest('.hidden'));
                if (!focusable.length) return;
                const first = focusable[0], last = focusable[focusable.length - 1];
                if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
                else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
            }
        });

        function goLogin() {
            const intent = sessionStorage.getItem('premier_post_auth_intent');
            window.location.href = intent === 'free-trial' ? '/?action=login&intent=free-trial' : '/?action=login';
        }

        function formatFreeTrialDate(value) {
            if (!value) return '';
            const date = new Date(value);
            return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('pt-BR');
        }

        function renderFreeTrialStatus(status) {
            const section = document.getElementById('teste-gratis');
            if (status?.hasPaidOrder || status?.eligible === false) {
                section.classList.add('hidden');
                return;
            }
            section.classList.remove('hidden');
            const badge = document.getElementById('freeTrialBadge');
            const message = document.getElementById('freeTrialMessage');
            const action = document.getElementById('freeTrialAction');
            const dates = document.getElementById('freeTrialDates');
            const key = status?.status || 'nao_solicitado';
            const states = {
                nao_solicitado: ['Disponível', 'Você ainda não solicitou o teste grátis. O pedido será registrado e analisado pela equipe.', 'Solicitar teste grátis', true, 'bg-blue-500/15 text-blue-300'],
                solicitado: ['Solicitado', 'Sua solicitação foi recebida e está aguardando liberação.', 'Solicitação registrada', false, 'bg-amber-500/15 text-amber-300'],
                liberado: ['Liberado', 'Seu teste foi liberado. A equipe usará o WhatsApp cadastrado para orientar o acesso.', 'Teste liberado', false, 'bg-green-500/15 text-green-300'],
                utilizado: ['Utilizado', 'Esta conta já utilizou o teste grátis.', 'Teste utilizado', false, 'bg-slate-700 text-slate-300'],
                recusado: ['Não liberado', 'Sua solicitação de teste grátis não foi liberada.', 'Solicitação não liberada', false, 'bg-red-500/15 text-red-300'],
                cancelado: ['Cancelado', 'A solicitação anterior foi cancelada antes do uso. Você pode solicitar novamente.', 'Solicitar novamente', true, 'bg-slate-700 text-slate-300']
            };
            const view = states[key] || states.nao_solicitado;
            const canRequest = view[3] && status?.canRequest !== false;
            badge.textContent = view[0];
            badge.className = `rounded-full px-3 py-1 text-xs font-bold ${view[4]}`;
            message.textContent = view[1];
            action.textContent = view[2];
            action.disabled = !canRequest;
            action.classList.toggle('opacity-50', !canRequest);
            action.classList.toggle('cursor-not-allowed', !canRequest);
            const items = [];
            if (status?.firstRequestedAt) items.push(`<span>Primeira solicitação: <strong class="text-slate-300">${formatFreeTrialDate(status.firstRequestedAt)}</strong></span>`);
            if (status?.lastRequestedAt && status.lastRequestedAt !== status.firstRequestedAt) items.push(`<span>Última solicitação: <strong class="text-slate-300">${formatFreeTrialDate(status.lastRequestedAt)}</strong></span>`);
            if (status?.releasedAt) items.push(`<span>Liberado: <strong class="text-slate-300">${formatFreeTrialDate(status.releasedAt)}</strong></span>`);
            if (status?.usedAt) items.push(`<span>Utilizado: <strong class="text-slate-300">${formatFreeTrialDate(status.usedAt)}</strong></span>`);
            dates.innerHTML = items.join('');
            dates.classList.toggle('hidden', items.length === 0);
            dates.classList.toggle('flex', items.length > 0);
        }

        function configureGuestFreeTrial() {
            document.getElementById('teste-gratis')?.classList.remove('hidden');
            renderFreeTrialStatus({ status: 'nao_solicitado' });
            const action = document.getElementById('freeTrialAction');
            action.textContent = 'Entrar para solicitar';
            action.disabled = false;
            action.onclick = () => {
                sessionStorage.setItem('premier_post_auth_intent', 'free-trial');
                goLogin();
            };
        }

        async function loadFreeTrialStatus() {
            const response = await fetch('/api/free-trial/me', { headers: { 'X-Session-Token': localStorage.getItem('premier_token') } });
            if (response.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
            if (!response.ok) throw new Error('Não foi possível consultar o teste grátis.');
            const status = await response.json();
            renderFreeTrialStatus(status);
            const action = document.getElementById('freeTrialAction');
            action.onclick = requestFreeTrial;
            return status;
        }

        async function requestFreeTrial() {
            if (!isLoggedIn) { configureGuestFreeTrial(); goLogin(); return; }
            const action = document.getElementById('freeTrialAction');
            const feedback = document.getElementById('freeTrialFeedback');
            action.disabled = true;
            action.textContent = 'Registrando...';
            feedback.classList.add('hidden');
            try {
                const response = await fetch('/api/free-trial/request', {
                    method: 'POST',
                    headers: window.premierMeta?.withAttributionHeaders({
                        'X-Session-Token': localStorage.getItem('premier_token')
                    }) || { 'X-Session-Token': localStorage.getItem('premier_token') }
                });
                const data = await response.json().catch(() => null);
                const status = data?.status?.status ? data.status : data?.status;
                if (response.status === 403 && status?.hasPaidOrder) {
                    renderFreeTrialStatus(status);
                    openFreeTrialIneligibleModal();
                    sessionStorage.removeItem('premier_post_auth_intent');
                    return;
                }
                if (!response.ok) throw new Error(data?.erro || 'Não foi possível registrar a solicitação.');
                renderFreeTrialStatus(status);
                feedback.textContent = data?.msg || 'Solicitação registrada.';
                feedback.className = 'mt-3 text-xs font-bold text-green-400';
                window.premierAnalytics?.track('free_trial_requested', { result: 'success' });
                window.premierMeta?.trackServerEvent(
                    'Lead',
                    { content_name: 'Teste gratuito Premier Host' },
                    data?.metaEventId
                );
                sessionStorage.removeItem('premier_post_auth_intent');
            } catch (error) {
                feedback.textContent = error.message || 'Não foi possível registrar a solicitação.';
                feedback.className = 'mt-3 text-xs font-bold text-red-400';
                action.disabled = false;
                action.textContent = 'Tentar novamente';
            }
        }

        function clearLocalSession() {
            localStorage.removeItem('premier_token');
            localStorage.removeItem('premier_user');
            localStorage.removeItem('premierAdmin');
            localStorage.removeItem('premier_isAdmin');
            localStorage.removeItem('premier_adminToken');
            Object.keys(sessionStorage)
                .filter(key => key.startsWith('premier_channel_welcome_'))
                .forEach(key => sessionStorage.removeItem(key));
        }

        function showLoginNotice(message = 'Faça login para continuar.') {
            const geralMsg = document.getElementById('geralErrorMsg');
            if (geralMsg) {
                geralMsg.innerText = message;
                geralMsg.className = 'text-xs text-amber-400 mt-2 text-center font-bold';
                geralMsg.classList.remove('hidden');
            }
        }

        function requireLogin(message = 'Para acessar este recurso, faça login na sua conta.') {
            showLoginNotice(message);
            document.getElementById('loginRequiredBanner')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
            return false;
        }

        function configureGuestAccess() {
            isLoggedIn = false;
            userData = null;
            eligibleForDiscount = false;
            pendingPixData = null;
            clearInterval(discountInterval);
            clearInterval(pixPollingInterval);
            configureGuestFreeTrial();

            document.getElementById('loginRequiredBanner')?.classList.remove('hidden');
            document.getElementById('loginRequiredBanner')?.classList.add('flex');
            document.getElementById('welcomeText').innerText = 'Configuração da Licença';

            const profileMenuText = document.getElementById('profileMenuText');
            if (profileMenuText) profileMenuText.innerText = 'Entrar';

            const profileMenuButton = document.getElementById('profileMenuButton');
            if (profileMenuButton) profileMenuButton.onclick = goLogin;
            document.getElementById('profileDropdown')?.classList.add('hidden');

            const mobileProfileButton = document.getElementById('mobileProfileButton');
            if (mobileProfileButton) {
                mobileProfileButton.innerText = 'Entrar';
                mobileProfileButton.onclick = (event) => {
                    event.preventDefault();
                    goLogin();
                };
            }
            document.getElementById('mobileLogoutButton')?.classList.add('hidden');

            const orderWa = document.getElementById('orderWa');
            if (orderWa) {
                orderWa.value = '';
                orderWa.placeholder = 'Disponível após login';
            }

            const btnEditOrderWa = document.getElementById('btnEditOrderWa');
            if (btnEditOrderWa) {
                btnEditOrderWa.innerText = 'Login';
                btnEditOrderWa.onclick = goLogin;
            }

            const refBlock = document.getElementById('referralBlock');
            refBlock.classList.remove('hidden');
            refBlock.classList.add('flex');
            document.getElementById('referralTitle').innerText = 'Indicações';
            document.getElementById('referralDescription').innerText = 'Faça login para criar, copiar e acompanhar seu código de indicação.';
            document.getElementById('referralCountBlock').classList.add('hidden');

            const refInput = document.getElementById('myReferralCode');
            refInput.value = '';
            refInput.placeholder = 'FAÇA LOGIN';
            refInput.readOnly = true;
            refInput.disabled = true;
            refInput.classList.add('cursor-not-allowed', 'text-slate-500');

            const refBtn = document.getElementById('btnSalvarRef');
            refBtn.innerText = 'Fazer login';
            refBtn.onclick = goLogin;

            document.getElementById('historyBlock').classList.add('hidden');
            document.getElementById('pendingPixBlock').classList.add('hidden');
            document.getElementById('pendingPixBlock').classList.remove('flex');

            const btnPagar = document.getElementById('btnPagar');
            btnPagar.innerText = 'Entrar para gerar Pix';
            btnPagar.disabled = false;

            calcular();
        }

        document.addEventListener("DOMContentLoaded", async () => {
            document.querySelectorAll('#qtdPcs, #qtdWyds, #qtdDias, input[name="periodo"]').forEach(control => {
                control.addEventListener('change', () => {
                    window.premierAnalytics?.track('simulation_changed', {
                        period: document.querySelector('input[name="periodo"]:checked')?.value || 'semanal',
                        computers: parseInt(document.getElementById('qtdPcs').value) || 1,
                        instances: parseInt(document.getElementById('qtdWyds').value) || 1,
                        days: parseInt(document.getElementById('qtdDias').value) || 7
                    });
                });
            });
            const userStr = localStorage.getItem('premier_user');
            const sessionToken = localStorage.getItem('premier_token');
            if (!userStr || !sessionToken) { configureGuestAccess(); return; }

            let localUser = null;
            try {
                localUser = JSON.parse(userStr);
            } catch {
                clearLocalSession();
                configureGuestAccess();
                return;
            }

            if (localStorage.getItem('premier_isAdmin') === 'true') {
                ['adminLink','mobileAdminLink'].forEach(id=>document.getElementById(id)?.classList.remove('hidden'));
            }

            // Busca dados REAIS da API ProfileController
            try {
                const res = await fetch(`/api/profile/${localUser.id}`, { headers: { 'X-Session-Token': sessionToken } });
                if (res.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
                if (!res.ok) throw new Error('Falha ao carregar perfil.');
                const data = await res.json();

                isLoggedIn = true;
                userData = data.user;
                eligibleForDiscount = data.eligibleForDiscount;
                discountExpiresAt = new Date(data.discountExpiresAt);
                document.getElementById('loginRequiredBanner')?.classList.add('hidden');
                document.getElementById('loginRequiredBanner')?.classList.remove('flex');
                document.getElementById('profileDropdown')?.classList.remove('hidden');
                const profileMenuText = document.getElementById('profileMenuText');
                if (profileMenuText) profileMenuText.innerText = 'Meu Perfil';
                const profileMenuButton = document.getElementById('profileMenuButton');
                if (profileMenuButton) profileMenuButton.onclick = null;
                const mobileProfileButton = document.getElementById('mobileProfileButton');
                if (mobileProfileButton) mobileProfileButton.innerText = 'Editar Perfil';
                document.getElementById('mobileLogoutButton')?.classList.remove('hidden');

                document.getElementById('welcomeText').innerText = `Olá, ${userData.name.split(' ')[0]}. Monte seu plano`;
                document.getElementById('orderWa').value = userData.whatsapp;
                const freeTrialIntent = new URLSearchParams(window.location.search).get('intent') === 'free-trial' || window.location.hash === '#teste-gratis' || sessionStorage.getItem('premier_post_auth_intent') === 'free-trial';
                const freeTrialStatus = await loadFreeTrialStatus();
                if (freeTrialIntent) {
                    sessionStorage.removeItem('premier_post_auth_intent');
                    if (freeTrialStatus?.hasPaidOrder) openFreeTrialIneligibleModal();
                    else requestAnimationFrame(() => document.getElementById('teste-gratis')?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
                }
                else abrirBoasVindasCanal();

                // Preenche Modal Perfil
                document.getElementById('profName').value = userData.name;
                document.getElementById('profEmail').value = userData.email;
                document.getElementById('profWa').value = userData.whatsapp;

                if (userData.referred_by_code) {
                    document.getElementById('divProfInvitedBy').classList.remove('hidden');
                    document.getElementById('profInvitedBy').value = userData.referred_by_code;
                } else {
                    document.getElementById('divProfInvitedBy').classList.add('hidden');
                }

                // Gerencia o Bloco de Referência (Sempre visível, mas readonly se já existir)
                const refBlock = document.getElementById('referralBlock');
                const refInput = document.getElementById('myReferralCode');
                const refBtn = document.getElementById('btnSalvarRef');

                refBlock.classList.remove('hidden');
                refBlock.classList.add('flex');
                refInput.disabled = false;
                refInput.classList.remove('cursor-not-allowed', 'text-slate-500');

                if (userData.referral_code) {
                    refInput.value = userData.referral_code;
                    refInput.readOnly = true;
                    refBtn.innerText = 'Copiar';
                    refBtn.dataset.cspClick = 'h133';
                } else {
                    refInput.value = '';
                    refInput.readOnly = false;
                    refBtn.innerText = 'Salvar';
                    refBtn.dataset.cspClick = 'h107';
                }
				carregarContagemIndicacao();

                if (eligibleForDiscount) iniciarTimerIndicacao();
                calcular();

                // Montar o histórico de pedidos na UI
                if (data.orders && data.orders.length > 0) {
                    const histBlock = document.getElementById('historyBlock');
                    const histList = document.getElementById('historyList');
                    histBlock.classList.remove('hidden');

                    histList.innerHTML = data.orders.map(o => {
                        const dStart = new Date(o.createdAt);
                        const dEnd = new Date(o.expiresAt);
                        const activeUntil = new Date(dEnd.getFullYear(), dEnd.getMonth(), dEnd.getDate() + 1);
                        const now = new Date();
                        const isCanceled = o.status === 'cancelado';
                        const isActive = !isCanceled && now < activeUntil;
                        const statusBadge = isCanceled
                            ? '<span class="text-slate-300 font-bold bg-slate-400/10 px-2 py-1 rounded text-xs">Cancelado</span>'
                            : (isActive
                                ? '<span class="text-green-400 font-bold bg-green-400/10 px-2 py-1 rounded text-xs">Ativo</span>'
                                : '<span class="text-red-400 font-bold bg-red-400/10 px-2 py-1 rounded text-xs">Expirado</span>');
                        const deliveryBadge = isCanceled
                            ? (o.refunded
                                ? '<span class="text-violet-300 font-bold bg-violet-400/10 px-2 py-1 rounded text-xs">Reembolsado</span>'
                                : '<span class="text-slate-400 font-bold bg-slate-400/10 px-2 py-1 rounded text-xs">Sem reembolso</span>')
                            : (o.delivered
                                ? '<span class="text-blue-300 font-bold bg-blue-400/10 px-2 py-1 rounded text-xs">Entregue</span>'
                                : '<span class="text-amber-300 font-bold bg-amber-400/10 px-2 py-1 rounded text-xs">Pendente</span>');
                        const canceledAt = o.canceledAt ? new Date(o.canceledAt) : null;
                        const dateSummary = isCanceled
                            ? `Cancelado em: ${(canceledAt || dStart).toLocaleDateString('pt-BR')}`
                            : `${isActive ? 'Vence' : 'Venceu'} em: ${dEnd.toLocaleDateString('pt-BR')}`;

                        return `
                        <div class="bg-slate-800 border border-slate-700 p-4 rounded-lg flex justify-between items-center">
                            <div>
                                <p class="text-sm font-bold text-white mb-1">${o.computers}x PCs · ${o.period}</p>
                                <p class="text-xs text-slate-400">Contratado em: ${dStart.toLocaleDateString('pt-BR')}</p>
                            </div>
                            <div class="text-right">
                                <div class="mb-1 flex justify-end gap-3">
                                    <div class="text-center">
                                        <p class="mb-1 text-[10px] font-semibold uppercase tracking-wide text-slate-500">Plano:</p>
                                        ${statusBadge}
                                    </div>
                                    ${!isCanceled || o.canceledWasPaid ? `<div class="text-center">
                                        <p class="mb-1 text-[10px] font-semibold uppercase tracking-wide text-slate-500">${isCanceled ? 'Pagamento:' : 'Entrega:'}</p>
                                        ${deliveryBadge}
                                    </div>` : ''}
                                </div>
                                <p class="text-xs text-slate-500">${dateSummary}</p>
                            </div>
                        </div>`;
                    }).join('');
                }

                // Checa se há um PIX Pendente (F5 seguro)
                checarPixPendente(localUser.id);

            } catch (e) {
                console.error("Erro ao puxar perfil:", e);
                configureGuestAccess();
            }
        });

		async function carregarContagemIndicacao() {
            if (!isLoggedIn) {
                document.getElementById('referralCountBlock').classList.add('hidden');
                return;
            }
			try {
				const user = JSON.parse(localStorage.getItem('premier_user'));
				if (!user || !user.id) {
					document.getElementById('referralCountBlock').classList.add('hidden');
					return;
				}

				const res = await fetch(`/api/profile/referral-count/${user.id}`, { headers: { 'X-Session-Token': localStorage.getItem('premier_token') } });
				if (res.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
				if (!res.ok) {
					document.getElementById('referralCountBlock').classList.add('hidden');
					return;
				}

				const data = await res.json();

				if (data.referralCode) {
					const countSpan = document.getElementById('referralCount');
					const countBlock = document.getElementById('referralCountBlock');
					countSpan.innerText = data.count;
					countBlock.classList.remove('hidden');
				} else {
					document.getElementById('referralCountBlock').classList.add('hidden');
				}
			} catch (err) {
				console.error('Erro ao carregar contagem:', err);
				document.getElementById('referralCountBlock').classList.add('hidden');
			}
		}

        async function checarPixPendente(userId) {
            if (!isLoggedIn || !userId) return;
            try {
                const res = await fetch(`/api/checkout/pending/${userId}`, {
                    headers: { 'X-Session-Token': localStorage.getItem('premier_token') }
                });
                if (res.ok) {
                    const data = await res.json();
                    if (!data.requiresPixGeneration) data.expiresAt = new Date(Date.now() + (data.expiresInSeconds * 1000)).toISOString();
                    pendingPixData = data;
                    document.getElementById('pendingPixBlock').classList.remove('hidden');
                    document.getElementById('pendingPixBlock').classList.add('flex');
                    document.getElementById('pendingPixValue').innerText = `R$ ${data.total.toFixed(2).replace('.', ',')}`;
                    document.getElementById('pendingPixDescription').innerText = data.requiresPixGeneration
                        ? 'Pedido criado pela equipe. Gere o PIX quando quiser concluir o pagamento.'
                        : 'Você tem um Pix aguardando pagamento.';
                    document.getElementById('pendingPixAction').innerText = data.requiresPixGeneration ? 'Gerar PIX' : 'Continuar pagamento';

                    const btn = document.getElementById('btnPagar');
                    btn.disabled = true;
                    btn.innerText = 'Você tem um pedido pendente acima';
                    btn.classList.replace('bg-blue-600', 'bg-slate-600');
                    btn.classList.replace('hover:bg-blue-500', 'hover:bg-slate-600');
                }
            } catch (e) {}
        }

        async function cancelarPixPendente() {
            if (!isLoggedIn) {
                requireLogin('Faça login para gerenciar pagamentos pendentes.');
                return;
            }
            if (!pendingPixData) return;
            try {
                const cancelUrl = pendingPixData.requiresPixGeneration
                    ? `/api/checkout/manual/${pendingPixData.orderId}/cancel`
                    : `/api/checkout/cancel/${pendingPixData.paymentId}`;
                const res = await fetch(cancelUrl, {
                    method: 'POST',
                    headers: { 'X-Session-Token': localStorage.getItem('premier_token') }
                });
                if (!res.ok) {
                    const data = await res.json().catch(() => ({}));
                    throw new Error(data.erro || 'Não foi possível cancelar o Pix.');
                }
            } catch (e) {
                const geralMsg = document.getElementById('geralErrorMsg');
                geralMsg.innerText = e.message;
                geralMsg.classList.remove('hidden');
                return;
            }

            pendingPixData = null;
            clearInterval(pixPollingInterval);
            document.getElementById('pendingPixBlock').classList.add('hidden');
            document.getElementById('pendingPixBlock').classList.remove('flex');
            fecharPixModal();

            const btn = document.getElementById('btnPagar');
            btn.disabled = false;
            btn.innerText = 'Gerar PIX';
            btn.classList.replace('bg-slate-600', 'bg-blue-600');
            btn.classList.replace('hover:bg-slate-600', 'hover:bg-blue-500');
        }

        async function retomarPix() {
            if (!isLoggedIn) {
                requireLogin('Faça login para retomar um PIX pendente.');
                return;
            }
            if (!pendingPixData) return;

            if (pendingPixData.requiresPixGeneration) {
                const action=document.getElementById('pendingPixAction');
                action.disabled=true;action.innerText='Gerando PIX...';
                try {
                    const response=await fetch(`/api/checkout/manual/${pendingPixData.orderId}/generate-pix`,{
                        method:'POST',
                        headers:window.premierMeta?.withAttributionHeaders({
                            'X-Session-Token':localStorage.getItem('premier_token')
                        })||{'X-Session-Token':localStorage.getItem('premier_token')}
                    });
                    const data=await response.json().catch(()=>({}));
                    if(response.status===401){clearLocalSession();configureGuestAccess();return;}
                    if(!response.ok)throw new Error(data.erro||'Não foi possível gerar o PIX.');
                    pendingPixData={paymentId:data.paymentId,total:data.total,encodedImage:data.encodedImage,payload:data.payload,expiresAt:new Date(Date.now()+(data.expiresInSeconds*1000)).toISOString()};
                    document.getElementById('pendingPixDescription').innerText='Você tem um Pix aguardando pagamento.';
                    action.innerText='Continuar pagamento';
                    window.premierAnalytics?.track('pix_created',{source:'manual_order'});
                    window.premierMeta?.trackServerEvent(
                        data.metaEvent?.eventName,
                        data.metaEvent?.customData,
                        data.metaEvent?.eventId
                    );
                } catch(e) {
                    const geralMsg=document.getElementById('geralErrorMsg');geralMsg.innerText=e.message;geralMsg.classList.remove('hidden');
                    action.disabled=false;action.innerText='Gerar PIX';return;
                }
                action.disabled=false;
            }

            document.getElementById('pixModalAmount').innerText = `R$ ${pendingPixData.total.toFixed(2).replace('.', ',')}`;
            document.getElementById('qrCodeImg').src = `data:image/png;base64,${pendingPixData.encodedImage}`;
            document.getElementById('pixCodeText').innerText = pendingPixData.payload;

            document.getElementById('pixScreen').classList.remove('hidden');
            document.getElementById('successScreen').classList.add('hidden');
            document.getElementById('pixModal').classList.remove('hidden');
            document.body.classList.add('overflow-hidden');

            const expiresAt = new Date(pendingPixData.expiresAt).getTime();
            const now = new Date().getTime();
            let secondsLeft = Math.floor((expiresAt - now) / 1000);
            if (secondsLeft < 0) secondsLeft = 0;

            iniciarPixPolling(pendingPixData.paymentId, secondsLeft);
        }

        function mascaraTelefone(input) { let v = input.value.replace(/\D/g, ''); if (v.length > 11) v = v.slice(0, 11); if (v.length > 2) v = `(${v.slice(0,2)}) ${v.slice(2)}`; if (v.length > 10) v = `${v.slice(0,10)}-${v.slice(10)}`; input.value = v; }

        async function salvarMeuCodigo() {
            if (!isLoggedIn || !userData) {
                requireLogin('Faça login para criar seu código de indicação.');
                const msg = document.getElementById('refErrorMsg');
                msg.innerText = 'Faça login para acessar suas indicações.';
                msg.classList.remove('hidden');
                return;
            }
            const input = document.getElementById('myReferralCode');
            const code = input.value.trim();
            if(code.length < 3) {
                // CORREÇÃO: Remoção agressiva da classe slate para garantir a exibição do vermelho no Tailwind JIT
                input.classList.remove('border-slate-700', 'focus:border-blue-500');
                input.classList.add('border-red-500', 'focus:border-red-500', 'focus:ring-1', 'focus:ring-red-500');
                document.getElementById('refErrorMsg').innerText = "Código muito curto.";
                document.getElementById('refErrorMsg').classList.remove('hidden');
                return;
            }

            try {
                const res = await fetch('/api/profile/referral', {
                    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Session-Token': localStorage.getItem('premier_token') },
                    body: JSON.stringify({ UserId: userData.id, Code: code })
                });
                if (res.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
                const data = await res.json();
                if(!res.ok) throw new Error(data.erro || "Este código já está em uso.");

                const btn = document.getElementById('btnSalvarRef');
                btn.innerText = 'Copiar';
                btn.classList.replace('bg-blue-600', 'bg-green-600');
                input.readOnly = true;
                btn.dataset.cspClick = 'h133';
                limparErro('myReferralCode', 'refErrorMsg');
                carregarContagemIndicacao(); // Atualiza contador imediatamente após salvar
            } catch(e) {
                input.classList.remove('border-slate-700', 'focus:border-blue-500');
                input.classList.add('border-red-500', 'focus:border-red-500', 'focus:ring-1', 'focus:ring-red-500');
                document.getElementById('refErrorMsg').innerText = e.message;
                document.getElementById('refErrorMsg').classList.remove('hidden');
            }
        }

        function copiarMeuCodigo() {
            if (!isLoggedIn || !userData) {
                requireLogin('Faça login para copiar seu código de indicação.');
                return;
            }
            navigator.clipboard.writeText(document.getElementById('myReferralCode').value);
            const btn = document.getElementById('btnSalvarRef');
            btn.innerText = 'Copiado!';
            setTimeout(() => { btn.innerText = 'Copiar'; }, 2000);
        }

        async function aplicarCupom() {
            const input = document.getElementById('cupomInput');
            const msg = document.getElementById('cupomMsg');
            const code = input.value.trim().toUpperCase();

            if(!code) return;

            try {
                const res = await fetch(`/api/checkout/cupom/${code}`);
                const data = await res.json();

                if(!res.ok) throw new Error(data.erro);

                cupomAtivo = data;
                msg.innerText = "Cupom aplicado!";
                msg.className = "text-xs mt-1 font-bold text-green-400";
                msg.classList.remove('hidden');
                input.readOnly = true;

                const btn = document.getElementById('btnCupom');
                btn.innerText = 'Remover';
                btn.classList.replace('bg-slate-800', 'bg-red-600');
                btn.classList.replace('hover:bg-blue-600', 'hover:bg-red-500');
                btn.dataset.cspClick = 'h134';

                calcular();

            } catch(e) {
                msg.innerText = e.message;
                msg.className = "text-xs mt-1 font-bold text-red-500";
                msg.classList.remove('hidden');
            }
        }

        function removerCupom() {
            cupomAtivo = null;
            const input = document.getElementById('cupomInput');
            const btn = document.getElementById('btnCupom');
            const msg = document.getElementById('cupomMsg');

            input.value = '';
            input.readOnly = false;

            btn.innerText = 'Aplicar';
            btn.classList.replace('bg-red-600', 'bg-slate-800');
            btn.classList.replace('hover:bg-red-500', 'hover:bg-blue-600');
            btn.dataset.cspClick = 'h118';

            msg.classList.add('hidden');
            document.getElementById('rowCupomExtra').classList.add('hidden');

            calcular();
        }

        function abrirPerfil() {
            if (!isLoggedIn || !userData) {
                goLogin();
                return;
            }
            document.getElementById('profileModal').classList.remove('hidden');
            document.body.classList.add('overflow-hidden');
            requestAnimationFrame(() => document.getElementById('profWa')?.focus());
        }
        function fecharPerfil() {
            document.getElementById('profileModal').classList.add('hidden');
            document.body.classList.remove('overflow-hidden');
            document.getElementById('profileMenuButton')?.focus();
        }
        async function salvarPerfil() {
            if (!isLoggedIn || !userData) {
                goLogin();
                return;
            }
            const btn = document.getElementById('saveProfileButton');
            btn.innerText = "Salvando...";
            btn.disabled = true;

            const passNew = document.getElementById('profPassNew').value;
            const passConfirm = document.getElementById('profPassConfirm').value;
            const passCurrent = document.getElementById('profPassCurrent').value;
            const whatsapp = document.getElementById('profWa').value;

            // Validações locais caso o usuário queira trocar a senha
            if (passNew.length > 0 || passConfirm.length > 0 || passCurrent.length > 0) {
                if (!passCurrent) {
                    showError('profPassCurrent', 'profErrorMsg');
                    document.getElementById('profErrorMsg').innerText = "Informe a senha atual.";
                    btn.innerText = "Salvar Alterações";
                    btn.disabled = false;
                    return;
                }
                if (passNew.length < 6 || passNew.length > 72) {
                    showError('profPassNew', 'profErrorMsg');
                    document.getElementById('profErrorMsg').innerText = "A nova senha deve ter no mínimo 6 caracteres.";
                    btn.innerText = "Salvar Alterações";
                    btn.disabled = false;
                    return;
                }
                if (passNew !== passConfirm) {
                    showError('profPassConfirm', 'profErrorMsg');
                    document.getElementById('profErrorMsg').innerText = "As senhas não coincidem.";
                    btn.innerText = "Salvar Alterações";
                    btn.disabled = false;
                    return;
                }
            }

            try {
                const res = await fetch(`/api/profile/${userData.id}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json', 'X-Session-Token': localStorage.getItem('premier_token') },
                    body: JSON.stringify({
                        Whatsapp: whatsapp,
                        CurrentPassword: passCurrent,
                        NewPassword: passNew
                    })
                });

                if (res.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
                const data = await res.json();
                if (!res.ok) {
                    showError('profWa', 'profErrorMsg');
                    document.getElementById('profErrorMsg').innerText = data.erro || "Falha ao salvar.";
                } else {
                    document.getElementById('orderWa').value = whatsapp;
                    document.getElementById('profSuccessMsg').classList.remove('hidden');
                    // Reset fields
                    document.getElementById('profPassCurrent').value = '';
                    document.getElementById('profPassNew').value = '';
                    document.getElementById('profPassConfirm').value = '';

                    setTimeout(() => { document.getElementById('profSuccessMsg').classList.add('hidden'); fecharPerfil(); }, 1500);
                }
            } catch (e) {
                showError('profWa', 'profErrorMsg');
                document.getElementById('profErrorMsg').innerText = "Erro de conexão.";
            } finally {
                btn.innerText = "Salvar Alterações";
                btn.disabled = false;
            }
        }

        function atualizarSlider(input) {
            if (!input) return;

            const min = Number(input.min);
            const max = Number(input.max);
            const value = Number(input.value);
            const control = input.closest('.range-control');
            const output = document.getElementById(`${input.id}Valor`);
            const dots = control?.querySelector('.range-dots');
            const steps = Math.max(1, max - min);

            control?.style.setProperty('--range-progress', `${((value - min) / steps) * 100}%`);
            if (output) output.value = String(value);

            if (dots && dots.dataset.range !== `${min}-${max}`) {
                dots.replaceChildren(...Array.from({ length: max - min + 1 }, (_, index) => {
                    const dot = document.createElement('span');
                    dot.className = 'range-dot';
                    dot.dataset.value = String(min + index);
                    return dot;
                }));
                dots.dataset.range = `${min}-${max}`;
            }

            dots?.querySelectorAll('.range-dot').forEach(dot => {
                dot.classList.toggle('is-active', Number(dot.dataset.value) <= value);
            });
        }

        function atualizarSliders() {
            document.querySelectorAll('.range-slider').forEach(atualizarSlider);
        }

        let lastPeriodo = 'semanal';

        async function calcular() {
            const rules = await getPricingRules();
            let periodo = document.querySelector('input[name="periodo"]:checked').value;
            let pcsInput = document.getElementById('qtdPcs');
            let wydsInput = document.getElementById('qtdWyds');
            pcsInput.min = String(periodo === 'diaria' ? rules.minDailyComputers : rules.minComputers);
            pcsInput.max = String(rules.maxComputers);
            wydsInput.min = String(rules.minSlots);
            wydsInput.max = String(rules.maxSlots);

            let pcs = parseInt(pcsInput.value);
            if (isNaN(pcs) || pcs < rules.minComputers) { pcsInput.value = rules.minComputers; pcs = rules.minComputers; }
            if (pcs > rules.maxComputers) { pcsInput.value = rules.maxComputers; pcs = rules.maxComputers; }
            let wyds = parseInt(wydsInput.value);
            if (isNaN(wyds) || wyds < rules.minSlots) { wydsInput.value = rules.minSlots; wyds = rules.minSlots; }

            if (wyds > rules.maxSlots) { wydsInput.value = rules.maxSlots; wyds = rules.maxSlots; }

            // Regra da Máquina de Estados
            if (lastPeriodo === 'diaria' && (periodo === 'semanal' || periodo === 'mensal')) {
                pcsInput.value = rules.minComputers; pcs = rules.minComputers;
                limparErro('qtdPcs', 'msgPcsAlert');
                limparErro('qtdDias', 'msgDiasAlert');
            }
            lastPeriodo = periodo;

            const contDias = document.getElementById('containerDias'), msgDiasAlert = document.getElementById('msgDiasAlert'), msgPcsAlert = document.getElementById('msgPcsAlert'), msgDesc = document.getElementById('msgDescPc');
            let bruto = 0, descPeriodo = 0, descHardware = 0, descIndicacao = 0, descCupom = 0, dias = null;
            const slotIncrement = (Math.max(wyds, rules.minSlots) - 1) * rules.additionalSlotPrice;
            const basePadrao = rules.weeklyBasePrice + slotIncrement;

            if (periodo === 'diaria') {
                contDias.classList.remove('hidden'); msgDesc.classList.add('hidden');

                // CORREÇÃO: Se estiver na diária, a reescrita agressiva joga pra 3 e remove a classe de erro nativa.
                if (pcs < rules.minDailyComputers) {
                    pcsInput.value = rules.minDailyComputers; pcs = rules.minDailyComputers;
                }
                msgPcsAlert.classList.add('hidden');
                limparErro('qtdPcs', 'msgPcsAlert');

                let diasInput = document.getElementById('qtdDias');
                dias = parseInt(diasInput.value);
                diasInput.min = String(rules.minDailyDays); diasInput.max = String(rules.maxDailyDays);
                if (isNaN(dias) || dias < rules.minDailyDays) { diasInput.value = rules.minDailyDays; dias = rules.minDailyDays; }

                if (dias > rules.maxDailyDays) {
                    msgDiasAlert.classList.remove('hidden');
                } else {
                    msgDiasAlert.classList.add('hidden');
                    limparErro('qtdDias', 'msgDiasAlert');
                }

                bruto = ((rules.dailyWeeklyBasePrice + slotIncrement) / rules.weeklyDays) * dias * pcs;

            } else {
                contDias.classList.add('hidden'); msgPcsAlert.classList.add('hidden'); msgDesc.classList.remove('hidden');
                msgDiasAlert.classList.add('hidden');
                limparErro('qtdPcs');
                limparErro('qtdDias');

                if (periodo === 'semanal') { bruto = basePadrao * pcs; }
                else if (periodo === 'mensal') { bruto = basePadrao * rules.monthlyWeeks * pcs; descPeriodo = bruto * rules.monthlyDiscountRate; }
                descHardware = (pcs - 1) * rules.additionalComputerDiscount; if (descHardware < 0) descHardware = 0;
            }

            atualizarSlider(pcsInput);
            atualizarSlider(wydsInput);
            atualizarSlider(document.getElementById('qtdDias'));

            // Aplicar 5% de desconto de Indicação
            if (eligibleForDiscount && bruto > 0) {
                descIndicacao = bruto * rules.referralDiscountRate;
                document.getElementById('rowIndicacao').classList.remove('hidden');
                document.getElementById('resDescIndicacao').innerText = `- R$ ${descIndicacao.toFixed(2).replace('.', ',')}`;
            }

            // Aplicar Cupom
            if (cupomAtivo && bruto > 0) {
                document.getElementById('rowCupomExtra').classList.remove('hidden');
                if (cupomAtivo.discount_type === 'percent') {
                    descCupom = bruto * (cupomAtivo.discount_value / 100);
                } else {
                    descCupom = cupomAtivo.discount_value;
                }
                document.getElementById('resDescCupom').innerText = `- R$ ${descCupom.toFixed(2).replace('.', ',')}`;
            }

            let totalLiq = bruto - descPeriodo - descHardware - descIndicacao - descCupom;
            if (totalLiq < 0) totalLiq = 0;

            let descArredonda = 0;
            const rowArredonda = document.getElementById('rowArredondamento');

            if (totalLiq > rules.commercialRoundingThreshold) {
                let vInt = Math.floor(totalLiq);
                descArredonda = totalLiq - vInt;
                totalLiq = vInt;
            }

            document.getElementById('resBase').innerText = `R$ ${bruto.toFixed(2).replace('.', ',')}`;
            document.getElementById('resDescHardware').innerText = `- R$ ${descHardware.toFixed(2).replace('.', ',')}`;
            document.getElementById('resDescPeriodo').innerText = `- R$ ${descPeriodo.toFixed(2).replace('.', ',')}`;

            const moeda = value => `R$ ${value.toFixed(2).replace('.', ',')}`;
            const moedaCompacta = value => `R$${value.toFixed(2).replace('.', ',')}`;
            const acessosAdicionais = Math.max(0, wyds - rules.minSlots);
            const custoComputadorDiaria = (rules.dailyWeeklyBasePrice / rules.weeklyDays) * dias;
            const subtotalComputadoresDiaria = custoComputadorDiaria * pcs;
            const custoTelaAdicionalDiaria = (rules.additionalSlotPrice / rules.weeklyDays) * dias;
            const subtotalTelasAdicionaisDiaria = custoTelaAdicionalDiaria * acessosAdicionais * pcs;
            const detalheTelasDiaria = acessosAdicionais > 0
                ? `\nTelas adicionais: ${acessosAdicionais} por computador (${moedaCompacta(custoTelaAdicionalDiaria)} por tela no período selecionado x ${pcs} ${pcs === 1 ? 'computador' : 'computadores'} = ${moedaCompacta(subtotalTelasAdicionaisDiaria)})`
                : '';
            const semanasPeriodo = periodo === 'mensal' ? rules.monthlyWeeks : 1;
            const detalheDuracaoPeriodo = periodo === 'mensal' ? ` (${rules.monthlyWeeks} semanas)` : '';
            const custoComputadorPeriodo = rules.weeklyBasePrice * semanasPeriodo;
            const subtotalComputadoresPeriodo = custoComputadorPeriodo * pcs;
            const custoTelaAdicionalPeriodo = rules.additionalSlotPrice * semanasPeriodo;
            const subtotalTelasAdicionaisPeriodo = custoTelaAdicionalPeriodo * acessosAdicionais * pcs;
            const detalheTelasPeriodo = acessosAdicionais > 0
                ? `\nTelas adicionais: ${acessosAdicionais} por computador (${moedaCompacta(custoTelaAdicionalPeriodo)} por tela no período selecionado x ${pcs} ${pcs === 1 ? 'computador' : 'computadores'} = ${moedaCompacta(subtotalTelasAdicionaisPeriodo)})`
                : '';
            const detalheBase = periodo === 'diaria'
                ? `Computadores: ${moeda(custoComputadorDiaria)} por computador × ${pcs} = ${moeda(subtotalComputadoresDiaria)} antes do arredondamento.${detalheTelasDiaria}`
                : `Computadores: ${moeda(custoComputadorPeriodo)} por computador${detalheDuracaoPeriodo} × ${pcs} = ${moeda(subtotalComputadoresPeriodo)}.${detalheTelasPeriodo}`;
            document.getElementById('resBaseInfo').textContent = detalheBase;
            document.getElementById('resDescHardwareInfo').textContent = periodo === 'diaria'
                ? 'O desconto por computadores adicionais não se aplica ao plano diário.'
                : `Desconto aplicado sobre ${Math.max(0, pcs - 1)} ${pcs - 1 === 1 ? 'computador adicional' : 'computadores adicionais'}.`;
            document.getElementById('resDescPeriodoInfo').textContent = `${(rules.monthlyDiscountRate * 100).toLocaleString('pt-BR')}% de desconto sobre o custo base do plano mensal.`;
            document.getElementById('rowDescPeriodo').classList.toggle('hidden', periodo !== 'mensal');
            document.getElementById('rowDescPeriodo').classList.toggle('flex', periodo === 'mensal');

            if (descArredonda > 0) { rowArredonda.classList.remove('hidden'); document.getElementById('resDescArredondamento').innerText = `- R$ ${descArredonda.toFixed(2).replace('.', ',')}`; }
            else { rowArredonda.classList.add('hidden'); }

            document.getElementById('totalPagar').innerText = `R$ ${totalLiq.toFixed(2).replace('.', ',')}`;
        }

        document.addEventListener('DOMContentLoaded', () => {
            atualizarSliders();

            const periodo = new URLSearchParams(window.location.search).get('periodo');
            const periodosValidos = ['diaria', 'semanal', 'mensal'];
            const opcao = periodosValidos.includes(periodo)
                ? document.querySelector(`input[name="periodo"][value="${periodo}"]`)
                : null;

            if (opcao) {
                opcao.checked = true;
                calcular();
            }

            if (window.location.hash === '#simular-planos') {
                setTimeout(() => {
                    document.getElementById('simular-planos')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
                }, 100);
            }
        });

        function iniciarTimerIndicacao() {
            discountInterval = setInterval(() => {
                const now = new Date().getTime();
                const distance = discountExpiresAt.getTime() - now;
                if (distance < 0) {
                    clearInterval(discountInterval);
                    eligibleForDiscount = false;
                    document.getElementById('rowIndicacao').classList.add('hidden');
                    calcular();
                    return;
                }
                const hours = Math.floor((distance % (1000 * 60 * 60 * 24)) / (1000 * 60 * 60));
                const minutes = Math.floor((distance % (1000 * 60 * 60)) / (1000 * 60));
                const seconds = Math.floor((distance % (1000 * 60)) / 1000);
                document.getElementById('timerIndicacao').innerText = `Expira em: ${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
            }, 1000);
        }

        // CORREÇÃO: Limpar os erros e as classes vermelhas fixando as classes neutras do Tailwind
        function limparErro(inputId, msgId) {
            const input = document.getElementById(inputId);
            if(input) {
                input.setAttribute('aria-invalid', 'false');
                input.classList.remove('border-red-500', 'focus:border-red-500', 'focus:ring-red-500', 'focus:ring-1');
                input.classList.add('border-slate-700', 'focus:border-blue-500');
            }
            if(msgId) {
                const msg = document.getElementById(msgId);
                if(msg) msg.classList.add('hidden');
            }
        }

        // CORREÇÃO: Força a remoção de qualquer classe neutra e impõe as do Tailwind com prioridade visual
        function showError(inputId, msgId) {
            const input = document.getElementById(inputId);
            if(input) {
                input.setAttribute('aria-invalid', 'true');
                input.classList.remove('border-slate-700', 'focus:border-blue-500');
                input.classList.add('border-red-500', 'focus:border-red-500', 'focus:ring-1', 'focus:ring-red-500');
                input.focus();
            }
            if(msgId) {
                const msg = document.getElementById(msgId);
                if(msg) msg.classList.remove('hidden');
            }
        }

        function marcarErroServidorWyd(message) {
            const input = document.getElementById('wydServerName');
            const msg = document.getElementById('wydServerErrorMsg');
            input.setAttribute('aria-invalid', 'true');
            input.classList.remove('border-slate-700', 'focus:border-blue-500');
            input.classList.add('border-red-500', 'focus:border-red-500', 'focus:ring-1', 'focus:ring-red-500');
            msg.innerText = message;
            msg.classList.remove('hidden');
        }

        async function validarServidorWyd(exibirPopup = false) {
            const input = document.getElementById('wydServerName');
            const serverName = input.value.trim();

            if (!serverName || serverName.length > 50) {
                marcarErroServidorWyd('Informe o nome do servidor de WYD com até 50 caracteres.');
                return false;
            }

            try {
                const response = await fetch('/api/checkout/validate-server', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ ServerName: serverName })
                });
                const data = await response.json().catch(() => ({}));
                if (input.value.trim() !== serverName) return true;
                if (!response.ok) {
                    marcarErroServidorWyd(data.erro || 'Não foi possível validar o servidor informado.');
                    if (exibirPopup && data.codigo === 'unsupported_wyd_server') abrirAvisoServidor();
                    return false;
                }

                limparErro('wydServerName', 'wydServerErrorMsg');
                return true;
            } catch {
                marcarErroServidorWyd('Não foi possível validar o servidor agora. Tente novamente.');
                return false;
            }
        }

        async function verificarERodarPix() {
            if (!isLoggedIn || !userData) {
                goLogin();
                return;
            }
            if (pendingPixData) return; // Se já tem pendente, trava.
            if (checkoutInFlight) return;
            checkoutInFlight = true;

            try {
                const pcsInput = document.getElementById('qtdPcs');
                const wydsInput = document.getElementById('qtdWyds');
                const diasInput = document.getElementById('qtdDias');
                const periodo = document.querySelector('input[name="periodo"]:checked').value;
                window.premierAnalytics?.track('checkout_attempted', {
                    period: periodo,
                    computers: parseInt(pcsInput.value) || 0,
                    instances: parseInt(wydsInput.value) || 0,
                    logged_in: true
                });

            // Restoring strict block if someone tries to hack the DOM/bypass input filters
            if (!pcsInput.value || parseInt(pcsInput.value) < 1) { showError('qtdPcs'); return; }
            if (parseInt(pcsInput.value) > 20) { showError('qtdPcs'); return; }
            if (periodo === 'diaria' && parseInt(pcsInput.value) < 3) { showError('qtdPcs', 'msgPcsAlert'); return; }
            if (!wydsInput.value || parseInt(wydsInput.value) < 1) { showError('qtdWyds'); return; }
            if (periodo === 'diaria' && (!diasInput.value || parseInt(diasInput.value) < 3)) { showError('qtdDias', 'msgDiasAlert'); return; }

            if (!await validarServidorWyd(true)) return;

            const anydesk = document.getElementById('anydeskId').value.trim();
            const wydServerName = document.getElementById('wydServerName').value.trim();
            const wa = document.getElementById('orderWa').value.trim();
            const geralMsg = document.getElementById('geralErrorMsg');

            if (anydesk.length < 6 || anydesk.length > 15) { showError('anydeskId', 'anydeskErrorMsg'); return; }
            geralMsg.classList.add('hidden');

            const btn = document.getElementById('btnPagar'), overlay = document.getElementById('loadingOverlay');
            btn.innerHTML = 'Conectando ao Asaas...'; btn.disabled = true; overlay.classList.remove('hidden');

            const total = parseFloat(document.getElementById('totalPagar').innerText.replace('R$ ', '').replace(',', '.'));
            const pcs = parseInt(pcsInput.value);
            const wyds = parseInt(wydsInput.value);

            // CORREÇÃO: Envio Exato da Variável do Período Pro Backend
            let diasEnvio = 30;
            if (periodo === 'diaria') diasEnvio = parseInt(diasInput.value) || 3;
            else if (periodo === 'semanal') diasEnvio = 7;

            try {
                const response = await fetch('/api/checkout/gerarpix', {
                    method: 'POST',
                    headers: {
                        ...(
                            window.premierMeta?.withAttributionHeaders({
                                'Content-Type': 'application/json',
                                'X-Session-Token': localStorage.getItem('premier_token')
                            }) || {
                                'Content-Type': 'application/json',
                                'X-Session-Token': localStorage.getItem('premier_token')
                            }
                        )
                    },
                    body: JSON.stringify({
                        UserId: userData.id,
                        Total: total,
                        AnydeskId: anydesk,
                        WydServerName: wydServerName,
                        Periodo: periodo,
                        Nome: userData.name,
                        Whatsapp: wa,
                        Pcs: pcs,
                        Wyds: wyds,
                        Dias: diasEnvio,
                        UsouDescontoIndicacao: eligibleForDiscount,
                        CodigoCupom: cupomAtivo?.code || ''
                    })
                });
                if (response.status === 401) { clearLocalSession(); configureGuestAccess(); return; }
                const data = await response.json();
                if (response.status === 409) {
                    await checarPixPendente(userData.id);
                    const pendingError = new Error(data.erro || 'Você já possui um pedido pendente.');
                    pendingError.isPendingConflict = true;
                    throw pendingError;
                }
                if (!response.ok && data.campo === 'wydServerName') {
                    marcarErroServidorWyd(data.erro || 'Revise o servidor de WYD informado.');
                    if (data.codigo === 'unsupported_wyd_server') abrirAvisoServidor();
                }
                if (!response.ok) {
                    const checkoutError = new Error(data.erro || "Erro de Gateway");
                    checkoutError.isFieldError = data.campo === 'wydServerName';
                    throw checkoutError;
                }

                pendingPixData = {
                    paymentId: data.paymentId,
                    total: data.total,
                    encodedImage: data.encodedImage,
                    payload: data.payload,
                    expiresAt: new Date(Date.now() + (data.expiresInSeconds * 1000)).toISOString()
                };

                document.getElementById('pendingPixBlock').classList.remove('hidden');
                document.getElementById('pendingPixBlock').classList.add('flex');
                document.getElementById('pendingPixValue').innerText = `R$ ${data.total.toFixed(2).replace('.', ',')}`;
                document.getElementById('pendingPixDescription').innerText = 'Você tem um Pix aguardando pagamento.';
                document.getElementById('pendingPixAction').innerText = 'Continuar pagamento';

                btn.disabled = true;
                btn.innerText = 'Você tem um pedido pendente acima';
                btn.classList.replace('bg-blue-600', 'bg-slate-600');
                btn.classList.replace('hover:bg-blue-500', 'hover:bg-slate-600');

                document.getElementById('pixModalAmount').innerText = `R$ ${data.total.toFixed(2).replace('.', ',')}`;
                document.getElementById('qrCodeImg').src = `data:image/png;base64,${data.encodedImage}`;
                document.getElementById('pixCodeText').innerText = data.payload;

                document.getElementById('pixScreen').classList.remove('hidden');
                document.getElementById('successScreen').classList.add('hidden');
                document.getElementById('pixModal').classList.remove('hidden');
                document.body.classList.add('overflow-hidden');
                window.premierAnalytics?.track('pix_created', {
                    period: periodo,
                    computers: pcs,
                    instances: wyds,
                    days: diasEnvio
                });
                window.premierMeta?.trackServerEvent(
                    data.metaEvent?.eventName,
                    data.metaEvent?.customData,
                    data.metaEvent?.eventId
                );

                // Queima o desconto visualmente do frontend, já que no Backend já foi queimado.
                if (eligibleForDiscount) {
                    eligibleForDiscount = false;
                    document.getElementById('rowIndicacao').classList.add('hidden');
                    calcular();
                }

                const secondsLeft = data.expiresInSeconds;
                iniciarPixPolling(data.paymentId, secondsLeft);

            } catch (error) {
                if (error.isPendingConflict) {
                    geralMsg.innerText = error.message;
                    geralMsg.classList.remove('hidden');
                } else if (!error.isFieldError) {
                    geralMsg.innerText = "Falha no gateway: " + error.message;
                    geralMsg.classList.remove('hidden');
                }
                window.premierAnalytics?.track('checkout_error', { error_code: 'pix_generation_failed', period: periodo });
                } finally {
                    if(!pendingPixData) {
                        btn.innerHTML = isLoggedIn ? 'Gerar PIX' : 'Faça login para gerar PIX';
                        btn.disabled = false;
                    }
                    overlay.classList.add('hidden');
                }
            } finally {
                checkoutInFlight = false;
            }
        }

        function iniciarPixPolling(paymentId, tempoSegundos = 900) {
            let tempo = tempoSegundos;
            const txt = document.getElementById('pixTimerText');
            clearInterval(pixPollingInterval);

            pixPollingInterval = setInterval(async () => {
                tempo--;
                const min = Math.floor(tempo / 60); const sec = tempo % 60;
                txt.innerText = `Expira em: ${min.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;

                if (tempo <= 0) {
                    clearInterval(pixPollingInterval);
                    txt.innerText = "Expirado";
                    cancelarPixPendente();
                    return;
                }

                if (tempo % 5 === 0) {
                    try {
                        const res = await fetch(`/api/checkout/status/${paymentId}`);
                        const s = await res.json();
                        if (s.status === 'pago') {
                            clearInterval(pixPollingInterval);
                            document.getElementById('pixScreen').classList.add('hidden');
                            document.getElementById('successScreen').classList.remove('hidden');
                            document.getElementById('pendingPixBlock').classList.add('hidden');
                            document.getElementById('pendingPixBlock').classList.remove('flex');
                        }
                    } catch {}
                }
            }, 1000);
        }

        function copiarPix() {
            const text = document.getElementById('pixCodeText').innerText;
            const btn = document.getElementById('btnCopiar');
            if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text); } else { let ta = document.createElement("textarea"); ta.value = text; ta.style.position = "fixed"; ta.style.left = "-999999px"; document.body.appendChild(ta); ta.focus(); ta.select(); try { document.execCommand('copy'); } catch (err) {} ta.remove(); }
            btn.innerHTML = '✓ Copiado com sucesso!'; btn.classList.replace('bg-slate-800', 'bg-blue-600');
            window.premierAnalytics?.track('pix_copied', { result: 'success' });
            setTimeout(() => { btn.innerHTML = 'Copiar código Pix'; btn.classList.replace('bg-blue-600', 'bg-slate-800'); }, 3000);
        }

        async function logout() {
            const token = localStorage.getItem('premier_token');
            try {
                if (token) {
                    await fetch('/api/auth/logout', {
                        method: 'POST',
                        headers: { 'X-Session-Token': token },
                        keepalive: true
                    });
                }
            } catch {}
            finally {
                clearLocalSession();
                window.location.href = '/';
            }
        }
