let _allLocalUsers = []; // Para edi&ccedil;&atilde;o
    let _pricingRules = null;
    let _manualPriceRequest = 0;
    let _adComputers = [];
    let _adAccessUser = '';
    let _adSelectedComputers = new Set();
    let _adAllowAllComputers = false;
    let _adAccessSortField = 'computer';
    let _adAccessSortDirection = 'asc';
    let _adGroups = [];
    let _adComputerGroups = {};
    let _adPendingGroupSelection = null;
    let _adLinkUsers = [];
    let _adLinkOptionsLoaded = false;
    let _adUsers = [];
    let _adSortField = 'fullName';
    let _adSortDirection = 'asc';
    let adminToastTimer = null;
    let adminConfirmResolver = null;
    let adminLastModalTrigger = null;
    let adminLogsRefreshTimer = null;

    const adminDeclarativeActions = Object.freeze({
        'admin-login': () => document.getElementById('admin-recovery-step') ? finishAdminLogin() : S.loginChallengeId ? doVerifyTwoFactor() : doLogin(),
        'select-ad-link-user': (_event, element) => selectAdLinkUser(element.dataset.username),
        'close-modals': () => closeModals(),
        'execute-delete-user': (_event, element) => executeDeleteUser(element.dataset.deleteAd === 'true'),
        'open-ad-access': (_event, element) => openAdAccessModal(element.dataset.username, element.dataset.computers, element.dataset.allowAll === 'true'),
        'toggle-ad-never': (_event, element) => toggleAdNever(element.dataset.username),
        'set-ad-expire': (_event, element) => setAdExpire(element.dataset.username),
        'open-ad-edit': (_event, element) => openAdEditModal(element.dataset.username),
        'open-ad-password': (_event, element) => openAdPasswordModal(element.dataset.username),
        'open-duplicate': (_event, element) => openDuplicateModal(element.dataset.username, element.dataset.fullName),
        'move-ou': (_event, element) => moveOu(element.dataset.username, element.dataset.archive === 'true'),
        'delete-ad-user': (_event, element) => deleteAdUser(element.dataset.username),
        'open-ad-computer-groups': (_event, element) => openAdComputerGroupsModal(element.dataset.computer),
        'edit-ad-computer': (_event, element) => editAdComputer(element.dataset.computer),
        'duplicate-ad-computer': (_event, element) => duplicateAdComputer(element.dataset.computer),
        'delete-ad-computer': (_event, element) => deleteAdComputer(element.dataset.computer),
        'edit-ad-group': (_event, element) => editAdGroup(element.dataset.group),
        'duplicate-ad-group': (_event, element) => duplicateAdGroup(element.dataset.group),
        'delete-ad-group': (_event, element) => deleteAdGroup(element.dataset.group),
        'load-ad': () => loadAd(),
        'set-ad-filter': (_event, element) => setAdFilter(element.dataset.filter, element),
        'toggle-ad-all': () => toggleAdAllComputers(),
        'sort-ad-access': (_event, element) => sortAdAccessComputers(element.dataset.field),
        'toggle-ad-computer': (_event, element) => toggleAdComputer(element),
        'start-maintenance': (_event, element) => startAdminMaintenance(element.dataset.operation),
        'close-maintenance-result': () => closeMaintenanceResult(),
        'toggle-order-delivery': (_event, element) => toggleOrderDelivery(element.dataset.orderId, element.checked, element),
        'mark-order-paid': (_event, element) => markOrderPaid(element.dataset.orderId),
        'delete-order': (_event, element) => deleteOrder(element.dataset.orderId),
        'open-cancel-order': (_event, element) => openCancelOrderModal(element.dataset.orderId, element.dataset.paid === 'true'),
        'show-registration-info': (_event, element) => showRegistrationInfo(element),
        'hide-registration-info': () => hideRegistrationInfo(),
        'open-ad-link': (_event, element) => openAdLinkModal(element.dataset.userId),
        'confirm-email': (_event, element) => confirmEmailFromCheckbox(element),
        'resend-email': (_event, element) => resendEmailConfirmation(element.dataset.userId, element),
        'open-local-user': (_event, element) => openUserModal(element.dataset.userId),
        'toggle-local-user': (_event, element) => toggleLocalUser(element.dataset.userId, element.dataset.activate === 'true', element),
        'delete-local-user': (_event, element) => deleteLocalUser(element.dataset.userId, element.dataset.hasAd === 'true'),
        'release-free-trial': (_event, element) => releaseFreeTrialManually(element.dataset.userId),
        'update-free-trial': (_event, element) => updateFreeTrial(element.dataset.requestId, element.dataset.updateAction, element.dataset.confirm),
        'delete-free-trial': (_event, element) => deleteFreeTrial(element.dataset.requestId, element.dataset.status),
        'load-page': (_event, element) => element.dataset.pageType === 'orders' ? loadOrders(Number(element.dataset.page)) : element.dataset.pageType === 'users' ? loadUsers(Number(element.dataset.page)) : loadFreeTrials(Number(element.dataset.page)),
        'select-whatsapp-template': (_event, element) => selectWhatsAppTemplate(element.dataset.templateKey),
        'insert-whatsapp-variable': (_event, element) => insertWhatsAppVariable(element.dataset.variable)
    });

    function dispatchAdminDeclarativeAction(eventType, event) {
        const origin = event.target instanceof Element ? event.target : null;
        const element = origin?.closest(`[data-admin-${eventType}]`);
        if (!element) return;
        const action = adminDeclarativeActions[element.getAttribute(`data-admin-${eventType}`)];
        if (action) action(event, element);
    }

    for (const eventType of ['click', 'change', 'input', 'focus', 'blur', 'mouseenter', 'mouseleave']) {
        const capture = ['focus', 'blur', 'mouseenter', 'mouseleave'].includes(eventType);
        document.addEventListener(eventType, (event) => dispatchAdminDeclarativeAction(eventType, event), capture);
    }

    function showAdminMessage(type, message) {
        const toast = document.getElementById('adminToast');
        if (!toast) return;
        toast.textContent = '';
        toast.setAttribute('role', type === 'success' ? 'status' : 'alert');
        toast.setAttribute('aria-live', type === 'success' ? 'polite' : 'assertive');

        const title = document.createElement('div');
        title.className = 'toast-title';
        title.textContent = type === 'success' ? 'Sucesso' : 'Erro';

        const body = document.createElement('div');
        body.className = 'toast-msg';
        body.textContent = message || (type === 'success' ? 'Operacao concluida.' : 'Nao foi possivel concluir a operacao.');

        toast.appendChild(title);
        toast.appendChild(body);
        toast.className = 'admin-toast show ' + (type === 'success' ? 'success' : 'error');

        clearTimeout(adminToastTimer);
        adminToastTimer = setTimeout(() => toast.classList.remove('show'), type === 'success' ? 2800 : 5200);
    }

    async function readResponseMessage(response, fallback) {
        const data = await response.json().catch(() => null);
        return data?.msg || data?.mensagem || data?.erro || fallback;
    }

    function askAdminConfirm(message, options = {}) {
        const modal = document.getElementById('modal-confirm');
        const msg = document.getElementById('confirm-message');
        document.getElementById('confirm-title').textContent = options.title || 'Confirmar ação';
        document.getElementById('confirm-cancel').textContent = options.cancelText || 'Cancelar';
        document.getElementById('confirm-accept').textContent = options.confirmText || 'Confirmar';
        msg.textContent = message;
        modal.classList.add('active');
        return new Promise(resolve => { adminConfirmResolver = resolve; });
    }

    function resolveAdminConfirm(value) {
        document.getElementById('modal-confirm').classList.remove('active');
        if (adminConfirmResolver) adminConfirmResolver(value);
        adminConfirmResolver = null;
    }

    function mascaraTelefone(input) { let v = input.value.replace(/\D/g, ''); if(v.length>11) v=v.slice(0,11); if(v.length>2) v=`(${v.slice(0,2)}) ${v.slice(2)}`; if(v.length>10) v=`${v.slice(0,10)}-${v.slice(10)}`; input.value=v; }

    function openUserModal(id) { 
        if(id) {
            document.getElementById('m-l-title').textContent = 'Editar usu\u00e1rio';
            document.getElementById('m-l-password-label').innerHTML = 'Senha (Deixe em branco para n&atilde;o alterar)';
            const u = _allLocalUsers.find(x => x.id === id);
            if(u) {
                document.getElementById('m-l-id').value = u.id;
                document.getElementById('m-l-name').value = u.name;
                document.getElementById('m-l-email').value = u.email;
                document.getElementById('m-l-whatsapp').value = u.whatsapp || '';
                document.getElementById('m-l-password').value = '';
            }
        } else {
            document.getElementById('m-l-title').textContent = 'Novo usu\u00e1rio';
            document.getElementById('m-l-password-label').innerHTML = 'Senha do Usu&aacute;rio';
            document.getElementById('m-l-id').value = '';
            document.getElementById('m-l-name').value = '';
            document.getElementById('m-l-email').value = '';
            document.getElementById('m-l-whatsapp').value = '';
            document.getElementById('m-l-password').value = '';
        }
        document.getElementById('modal-local-user').classList.add('active'); 
    }

    function openAdUserModal() {
        document.getElementById('m-ad-username').value = '';
        document.getElementById('m-ad-fullname').value = '';
        document.getElementById('m-ad-whatsapp').value = '';
        document.getElementById('m-ad-password').value = '';
        document.getElementById('modal-ad-user').classList.add('active'); 
    }

    async function openAdEditModal(username) {
        document.getElementById('m-ad-edit-title').textContent = 'Editar: ' + username;
        document.getElementById('m-ad-edit-username').value = username;
        document.getElementById('m-ad-edit-fullname').value = 'Carregando...';
        document.getElementById('m-ad-edit-email-label').textContent = 'E-mail';
        document.getElementById('m-ad-edit-email').value = '';
        document.getElementById('m-ad-edit-whatsapp').value = '';
        document.getElementById('m-ad-edit-password').value = '';
        document.getElementById('m-ad-edit-active').checked = false;
        document.getElementById('m-ad-edit-never-expires').checked = false;
        document.getElementById('modal-ad-edit').classList.add('active');

        const u = await apiFetch('/api/admin/ad/users/' + encodeURIComponent(username));
        if (u) {
            document.getElementById('m-ad-edit-fullname').value = u.fullName || '';
            document.getElementById('m-ad-edit-email-label').textContent = u.emailFromLocalFallback
                ? 'E-mail (será atualizado)'
                : 'E-mail';
            document.getElementById('m-ad-edit-email').value = u.email || '';
            document.getElementById('m-ad-edit-whatsapp').value = u.telephoneNumber || '';
            document.getElementById('m-ad-edit-active').checked = u.isActive;
            document.getElementById('m-ad-edit-never-expires').checked = u.passwordNeverExpires;
        } else {
            closeModals();
            showAdminMessage('error', 'Falha ao buscar dados do usuário.');
        }
    }

    function openAdPasswordModal(username) {
        document.getElementById('m-ad-password-title').textContent = 'Redefinir senha: ' + username;
        document.getElementById('m-ad-password-username').value = username;
        document.getElementById('m-ad-new-password').value = '';
        document.getElementById('m-ad-confirm-password').value = '';
        document.getElementById('m-ad-force-change').checked = true;
        document.getElementById('modal-ad-password').classList.add('active');
    }

    async function submitAdPassword() {
        const username = document.getElementById('m-ad-password-username').value;
        const newPass = document.getElementById('m-ad-new-password').value;
        const confirmPass = document.getElementById('m-ad-confirm-password').value;
        const forceChange = document.getElementById('m-ad-force-change').checked;
        if (!newPass) { showAdminMessage('error', 'Informe a nova senha.'); return; }
        if (newPass !== confirmPass) { showAdminMessage('error', 'As senhas n\u00e3o coincidem.'); return; }
        const r = await fetch('/api/admin/ad/users/' + encodeURIComponent(username) + '/password', {
            method: 'PUT', headers: hdrs(),
            body: JSON.stringify({ Password: newPass, ForceChangeOnNextLogon: forceChange })
        });
        const msg = await readResponseMessage(r, 'Senha redefinida com sucesso.');
        if (r.ok) { showAdminMessage('success', msg); closeModals(); }
        else showAdminMessage('error', msg);
    }

    function openDuplicateModal(username, fullname) {
        document.getElementById('m-ad-dup-source').value = username;
        document.getElementById('m-ad-dup-username').value = '';
        document.getElementById('m-ad-dup-fullname').value = '';
        document.getElementById('m-ad-dup-whatsapp').value = '';
        document.getElementById('m-ad-dup-password').value = '';
        document.getElementById('m-ad-dup-confirm').value = '';
        document.getElementById('m-ad-dup-title').innerHTML = `Duplicar usuário: <b>${esc(username)}</b>`;
        document.getElementById('modal-ad-duplicate').classList.add('active');
    }

    async function submitDuplicate() {
        const sourceUsername = document.getElementById('m-ad-dup-source').value;
        const newUsername = document.getElementById('m-ad-dup-username').value.trim();
        const newFullName = document.getElementById('m-ad-dup-fullname').value.trim();
        const wa = document.getElementById('m-ad-dup-whatsapp').value.trim();
        const pass = document.getElementById('m-ad-dup-password').value;
        const conf = document.getElementById('m-ad-dup-confirm').value;

        if (!newUsername || !newFullName || !pass) {
            showAdminMessage('error', 'Preencha username, nome completo e senha.');
            return;
        }
        if (pass !== conf) {
            showAdminMessage('error', 'As senhas não coincidem.');
            return;
        }

        const r = await fetch(`/api/admin/ad/users/${encodeURIComponent(sourceUsername)}/duplicate`, {
            method: 'POST',
            headers: hdrs(),
            body: JSON.stringify({
                NewUsername: newUsername,
                NewFullName: newFullName,
                Password: pass,
                Whatsapp: wa
            })
        });

        const msg = await readResponseMessage(r, 'Usuário duplicado com sucesso.');
        if (r.ok) {
            showAdminMessage('success', msg);
            closeModals();
            loadAd();
        } else {
            showAdminMessage('error', msg);
        }
    }

    function closeModals() {
        document.querySelectorAll('.modal-overlay').forEach(e=>e.classList.remove('active'));
        closeAdLinkDropdown();
        adminLastModalTrigger?.focus();
    }

    function enhanceAdminModals() {
        document.querySelectorAll('.modal-overlay').forEach((modal, index) => {
            modal.setAttribute('role', 'dialog');
            modal.setAttribute('aria-modal', 'true');
            const title = modal.querySelector('.modal-title');
            if (title) {
                if (!title.id) title.id = `admin-modal-title-${index}`;
                modal.setAttribute('aria-labelledby', title.id);
            }
            modal.querySelectorAll('label').forEach(label => {
                if (label.htmlFor) return;
                const control = label.parentElement?.querySelector('input,select,textarea');
                if (control?.id) label.htmlFor = control.id;
            });
        });
    }

    async function openManualOrderModal() {
        await Promise.all([loadLocalUsersSnapshot(),loadPricingRules()]);
        const sel = document.getElementById('m-order-user');
        sel.innerHTML = '<option value="">-- Selecione o Cliente --</option>' + 
            (_allLocalUsers || []).map(u => `<option value="${u.id}">${esc(u.name)} (${esc(u.email)})</option>`).join('');
        
        document.getElementById('m-order-period').value = 'semanal';
        document.getElementById('m-order-days').value = _pricingRules.weeklyDays;
        document.getElementById('m-order-pcs').value = _pricingRules.minComputers;
        document.getElementById('m-order-slots').value = _pricingRules.minSlots;
        document.getElementById('m-order-price').value = Number(_pricingRules.minimumPrices.semanal).toFixed(2);
        document.getElementById('m-order-anydesk').value = "";
        document.getElementById('m-order-server').value = "";
        
        document.getElementById('manualOrderModal').classList.add('active');
    }

    async function loadPricingRules(){
        if(_pricingRules)return _pricingRules;
        const response=await fetch('/api/checkout/pricing-rules');
        if(!response.ok)throw new Error('Não foi possível carregar as regras de preço.');
        _pricingRules=await response.json();return _pricingRules;
    }

    async function syncManualOrderPeriod(){
        await loadPricingRules();
        const period=document.getElementById('m-order-period').value,days=document.getElementById('m-order-days'),pcs=document.getElementById('m-order-pcs');
        if(period==='semanal')days.value=_pricingRules.weeklyDays;
        else if(period==='mensal')days.value=_pricingRules.monthlyDays;
        else{days.value=_pricingRules.minDailyDays;if(parseInt(pcs.value)<_pricingRules.minDailyComputers)pcs.value=_pricingRules.minDailyComputers;}
        updateManualOrderSuggestedPrice();
    }

    async function updateManualOrderSuggestedPrice(){
        const requestId=++_manualPriceRequest;
        const payload={period:document.getElementById('m-order-period').value,days:parseInt(document.getElementById('m-order-days').value),computers:parseInt(document.getElementById('m-order-pcs').value),slots:parseInt(document.getElementById('m-order-slots').value)};
        const response=await fetch('/api/checkout/pricing-quote',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
        const data=await response.json().catch(()=>({}));if(requestId!==_manualPriceRequest)return;
        if(response.ok)document.getElementById('m-order-price').value=Number(data.total).toFixed(2);
        else showAdminMessage('error',data.erro||'Não foi possível calcular o valor sugerido.');
    }

    async function saveManualOrder() {
        const userId = document.getElementById('m-order-user').value;
        if (!userId) return showAdminMessage('error', 'Selecione um cliente.');
        const anyDeskId=document.getElementById('m-order-anydesk').value.replace(/\D/g,'');
        const wydServerName=document.getElementById('m-order-server').value.trim();
        if(anyDeskId.length<6||anyDeskId.length>15)return showAdminMessage('error','Informe um ID do AnyDesk válido, com 6 a 15 números.');
        if(!wydServerName)return showAdminMessage('error','Informe o servidor WYD.');

        const req = {
            userId,
            anyDeskId,
            wydServerName,
            period: document.getElementById('m-order-period').value,
            days: parseInt(document.getElementById('m-order-days').value) || 7,
            computers: parseInt(document.getElementById('m-order-pcs').value) || 1,
            wydsPerComputer: parseInt(document.getElementById('m-order-slots').value) || 1,
            totalPrice: parseFloat(document.getElementById('m-order-price').value) || 0
        };

        const r = await fetch('/api/admin/orders/manual', {
            method: 'POST',
            headers: hdrs(),
            body: JSON.stringify(req)
        });

        const msg = await readResponseMessage(r, 'Pedido manual criado com sucesso.');
        if (r.ok) {
            closeModals();
            showAdminMessage('success', msg);
            loadOrders();
        } else {
            showAdminMessage('error', msg);
        }
    }
    
    async function loadLocalUsersSnapshot() {
        const first = await apiFetch(API + '/users?page=1&limit=100&search=&sortBy=name&sortDir=asc');
        if(!first){_allLocalUsers=[];return _allLocalUsers;}
        _allLocalUsers = [...(first.users || [])];
        const pages = Math.ceil((first.total || 0) / 100);
        for(let page=2;page<=pages;page++){
            const data = await apiFetch(API + `/users?page=${page}&limit=100&search=&sortBy=name&sortDir=asc`);
            if(!data)break;
            _allLocalUsers.push(...(data.users || []));
        }
        return _allLocalUsers;
    }
    
    async function submitLocalUser() {
        const id = document.getElementById('m-l-id').value;
        const body = { 
            Name: document.getElementById('m-l-name').value, 
            Email: document.getElementById('m-l-email').value, 
            Whatsapp: document.getElementById('m-l-whatsapp').value, 
            Password: document.getElementById('m-l-password').value 
        };
        
        let url = '/api/admin/users';
        let method = 'POST';
        if(id) { url += '/' + id; method = 'PUT'; }

        const r = await fetch(url, { method, headers: hdrs(), body: JSON.stringify(body) });
        const msg = await readResponseMessage(r, id ? 'Usuario atualizado.' : 'Usuario criado.');
        if(r.ok) { showAdminMessage('success', msg); closeModals(); loadUsers(); } else { showAdminMessage('error', msg); }
    }

    async function confirmEmail(id, checkbox) {
        if(checkbox) checkbox.disabled = true;
        const r = await fetch('/api/admin/users/'+id+'/confirm-email', { method: 'PUT', headers: hdrs() });
        const msg = await readResponseMessage(r, 'E-mail confirmado manualmente.');
        if(r.ok) { showAdminMessage('success', msg); loadUsers(); } else { showAdminMessage('error', msg); if(checkbox){ checkbox.checked = false; checkbox.disabled = false; } }
    }

    function confirmEmailFromCheckbox(checkbox) {
        confirmEmail(checkbox.dataset.userId, checkbox);
    }

    async function resendEmailConfirmation(id, button, force = false) {
        if(button) button.disabled = true;
        try {
            const url = '/api/admin/users/'+id+'/resend-confirmation'+(force?'?force=true':'');
            const r = await fetch(url, { method: 'POST', headers: hdrs() });
            const data = await r.json().catch(() => null);
            if(r.status === 409 && data?.requiresConfirmation) {
                const sentAt = data.lastSentAt ? fmtDateTime(data.lastSentAt) : 'hoje';
                const proceed = await askAdminConfirm(
                    `Um e-mail de confirmação já foi enviado hoje, em ${sentAt}. Para evitar mensagens duplicadas, recomendamos aguardar. Deseja reenviar mesmo assim?`,
                    { title: 'E-mail já enviado hoje', confirmText: 'Continuar mesmo assim', cancelText: 'Cancelar' }
                );
                if(proceed) return resendEmailConfirmation(id, button, true);
                return;
            }
            const msg = data?.msg || data?.mensagem || data?.erro || 'E-mail de confirmação reenviado.';
            if(r.ok) showAdminMessage('success', msg);
            else showAdminMessage('error', msg);
        } finally {
            if(button) button.disabled = false;
        }
    }
    
    async function deleteOrder(id) {
        if(await askAdminConfirm('Tem certeza que deseja excluir este pedido permanentemente?')) {
            const r = await fetch('/api/admin/orders/' + id, { method: 'DELETE', headers: hdrs() });
            const msg = await readResponseMessage(r, 'Pedido excluído.');
            if(r.ok) { showAdminMessage('success', msg); loadOrders(); } else showAdminMessage('error', msg);
        }
    }

    async function markOrderPaid(id) {
        if(await askAdminConfirm('Marcar este pedido como pago manualmente? Se houver PIX pendente na Asaas, ele sera cancelado para evitar pagamento duplicado.')) {
            const r = await fetch('/api/admin/orders/' + id + '/mark-paid', { method: 'PUT', headers: hdrs() });
            const msg = await readResponseMessage(r, 'Pedido marcado como pago.');
            if(r.ok) { showAdminMessage('success', msg); loadOrders(); } else showAdminMessage('error', msg);
        }
    }

    async function toggleLocalUser(id, isActive, button) {
        if(button) button.disabled = true;
        const r = await fetch('/api/admin/users/'+id+'/active', { method: 'PUT', headers: hdrs(), body: JSON.stringify({IsActive:isActive}) });
        const msg = await readResponseMessage(r, isActive ? 'Usuario ativado.' : 'Usuario inativado.');
        if(r.ok) { showAdminMessage('success', msg); loadUsers(); } else { showAdminMessage('error', msg); if(button) button.disabled = false; }
    }

    async function openAdLinkModal(id) {
        const u = _allLocalUsers.find(x => x.id === id);
        if(!u) return;
        document.getElementById('m-ad-link-id').value = id;
        document.getElementById('m-ad-link-local').textContent = u.name + ' - ' + u.email;
        document.getElementById('m-ad-link-username').value = u.adUsername || '';
        document.getElementById('modal-ad-link').classList.add('active');

        await loadAdLinkOptions();
        renderAdLinkDropdown(false);
    }

    async function loadAdLinkOptions() {
        if(_adLinkOptionsLoaded) return;
        const users = await apiFetch('/api/admin/ad/users');
        if(Array.isArray(users)) {
            const allowedOus = new Set(['USUARIOS', 'USUARIOS_EXPIRADOS', 'USUARIOS_WEBSITE']);
            _adLinkUsers = users
                .filter(x => allowedOus.has(x.ouPath))
                .sort((a, b) => String(a.username || '').localeCompare(String(b.username || ''), 'pt-BR', {sensitivity:'base'}));
            _adLinkOptionsLoaded = true;
        }
    }

    async function openAdLinkDropdown() {
        await loadAdLinkOptions();
        renderAdLinkDropdown(true);
    }

    async function toggleAdLinkDropdown() {
        const menu = document.getElementById('ad-link-dropdown');
        if(menu?.classList.contains('open')) {
            closeAdLinkDropdown();
            return;
        }
        await openAdLinkDropdown();
    }

    function renderAdLinkDropdown(openMenu = true) {
        const menu = document.getElementById('ad-link-dropdown');
        const input = document.getElementById('m-ad-link-username');
        if(!menu || !input) return;

        const q = normalizeComputerName(input.value);
        const list = _adLinkUsers
            .filter(x => !q || normalizeComputerName(x.username).includes(q) || normalizeComputerName(x.fullName).includes(q))
            .slice(0, 80);

        if(!list.length) {
            menu.innerHTML = '<div class="ad-link-empty">Nenhum usu&aacute;rio AD encontrado nas pastas de ativos, expirados ou website.</div>';
        } else {
            menu.innerHTML = list.map(x => {
                const folder = ({USUARIOS:'Ativos',USUARIOS_EXPIRADOS:'Expirados',USUARIOS_WEBSITE:'Website'})[x.ouPath] || x.ouPath || 'AD';
                const label = (x.fullName || x.username) + ' · ' + folder;
                return `<button type="button" class="ad-link-option" data-username="${esc(x.username)}" data-admin-click="select-ad-link-user">
                    <span class="ad-link-option-main">${esc(x.username)}</span>
                    <span class="ad-link-option-sub">${esc(label)}</span>
                </button>`;
            }).join('');
        }

        menu.classList.toggle('open', openMenu);
    }

    function closeAdLinkDropdown() {
        document.getElementById('ad-link-dropdown')?.classList.remove('open');
    }

    function selectAdLinkUser(username) {
        document.getElementById('m-ad-link-username').value = username || '';
        closeAdLinkDropdown();
    }

    async function submitAdLink() {
        const id = document.getElementById('m-ad-link-id').value;
        const adUsername = document.getElementById('m-ad-link-username').value.trim();
        const r = await fetch('/api/admin/users/'+id+'/ad-link', { method: 'PUT', headers: hdrs(), body: JSON.stringify({AdUsername: adUsername}) });
        const msg = await readResponseMessage(r, 'Vinculo AD atualizado.');
        if(r.ok) { showAdminMessage('success', msg); closeModals(); loadUsers(); } else showAdminMessage('error', msg);
    }

    function openCancelOrderModal(id, isPaid) {
        document.getElementById('cancel-order-id').value = id;
        document.getElementById('btn-cancel-refund').style.display = isPaid ? 'inline-flex' : 'none';
        document.getElementById('btn-cancel-no-refund').textContent = isPaid ? 'Cancelar sem reembolso' : 'Cancelar pedido';
        document.getElementById('cancel-order-note').textContent = isPaid
            ? 'Este pedido está pago. Você pode cancelar apenas no sistema ou cancelar e solicitar o reembolso no Asaas. Ambas as opções encerram o pedido.'
            : 'Este pedido ainda não está pago. O cancelamento encerrará o Pix pendente e removerá o pedido da fila operacional.';
        document.getElementById('modal-cancel-order').classList.add('active');
    }

    async function confirmCancelOrder(refund) {
        const id = document.getElementById('cancel-order-id').value;
        const btn1 = document.getElementById('btn-cancel-no-refund');
        const btn2 = document.getElementById('btn-cancel-refund');
        btn1.disabled = true; btn2.disabled = true;
        
        try {
            const r = await fetch('/api/admin/orders/'+id+'/cancel?refund='+refund, { method: 'DELETE', headers: hdrs() });
            const msg = await readResponseMessage(r, 'Pedido cancelado.');
            if(r.ok) { showAdminMessage('success', msg); closeModals(); loadOrders(); } else { showAdminMessage('error', msg); }
        } finally {
            btn1.disabled = false; btn2.disabled = false;
        }
    }

    async function toggleOrderDelivery(id, delivered, checkbox) {
        if(checkbox) checkbox.disabled = true;
        const r = await fetch('/api/admin/orders/'+id+'/delivery', { method: 'PUT', headers: hdrs(), body: JSON.stringify({Delivered: delivered}) });
        const msg = await readResponseMessage(r, delivered ? 'Pedido entregue.' : 'Pedido pendente.');
        if(r.ok) { showAdminMessage('success', msg); loadOrders(); } else { showAdminMessage('error', msg); if(checkbox){ checkbox.checked = !delivered; checkbox.disabled = false; } }
    }

    let _userToDelete = null;
    function deleteLocalUser(id, hasAd) {
        _userToDelete = id;
        document.getElementById('delete-user-ad-note').style.display = hasAd ? 'block' : 'none';
        
        const footer = document.getElementById('delete-user-footer');
        if (hasAd) {
            footer.innerHTML = `
                <button class="btn btn-outline" data-admin-click="close-modals">Cancelar</button>
                <button class="btn btn-outline csp-d001" data-admin-click="execute-delete-user" data-delete-ad="false">Excluir apenas do Site</button>
                <button class="btn btn-outline csp-d002" data-admin-click="execute-delete-user" data-delete-ad="true">Excluir do Site e AD</button>
            `;
        } else {
            footer.innerHTML = `
                <button class="btn btn-outline" data-admin-click="close-modals">Cancelar</button>
                <button class="btn btn-outline csp-d002" data-admin-click="execute-delete-user" data-delete-ad="false">Excluir Usuário</button>
            `;
        }
        
        document.getElementById('modal-delete-user').classList.add('active');
    }

    async function executeDeleteUser(deleteAd) {
        const id = _userToDelete;
        closeModals();
        const r = await fetch('/api/admin/users/'+id+'?deleteAd='+deleteAd, { method: 'DELETE', headers: hdrs() });
        const msg = await readResponseMessage(r, 'Usuario excluido.');
        if(r.ok) { showAdminMessage('success', msg); loadUsers(); } else showAdminMessage('error', msg);
    }

    let currentAdFilter = 'users';
    function setAdFilter(f, btn) {
        currentAdFilter = f;
        document.querySelectorAll('#ad-fbar .fb').forEach(e=>e.classList.remove('active'));
        btn?.classList.add('active');
        document.getElementById('ad-users-tbl').classList.add('hidden');
        document.getElementById('ad-website-tbl').classList.add('hidden');
        document.getElementById('ad-expired-tbl').classList.add('hidden');
        document.getElementById('ad-groups-tbl').classList.add('hidden');
        document.getElementById('ad-computers-tbl').classList.add('hidden');
        document.getElementById('ad-'+f+'-tbl').classList.remove('hidden');
        syncAdCreationActions();
        ensureAdSortForFilter();
        renderAdCollections();
        updateSortableHeaderState();
    }

    function syncAdCreationActions(){
        document.getElementById('ad-new-user')?.classList.toggle('hidden',currentAdFilter==='groups'||currentAdFilter==='computers');
        document.getElementById('ad-new-group')?.classList.toggle('hidden',currentAdFilter!=='groups');
        document.getElementById('ad-new-computer')?.classList.toggle('hidden',currentAdFilter!=='computers');
    }

    function suggestedAdCopyName(name,maxLength,items){
        const existing=new Set(items.map(item=>String(item.name||'').toLocaleUpperCase('pt-BR')));
        const base=String(name||'').slice(0,Math.max(1,maxLength-6));
        let candidate=(base+'-COPIA').slice(0,maxLength),index=2;
        while(existing.has(candidate.toLocaleUpperCase('pt-BR'))){
            const suffix='-'+index++;
            candidate=(base.slice(0,maxLength-suffix.length)+suffix).slice(0,maxLength);
        }
        return candidate;
    }
    function openAdGroupModal(group=null,duplicate=false){
        const editing=group&&!duplicate;
        document.getElementById('m-ad-group-original').value=editing?group.name:'';
        document.getElementById('m-ad-group-title').textContent=editing?'Editar Grupo do Active Directory':duplicate?'Duplicar Grupo do Active Directory':'Novo Grupo do Active Directory';
        document.getElementById('m-ad-group-name').value=group?(duplicate?suggestedAdCopyName(group.name,20,_adGroups):group.name):'';
        document.getElementById('m-ad-group-description').value=group?.description||'';
        document.getElementById('btn-create-ad-group').textContent=editing?'Salvar alterações':duplicate?'Duplicar grupo':'Criar grupo';
        document.getElementById('m-ad-group-note').textContent=editing?'Nome, descrição e vínculo do grupo serão atualizados no Active Directory.':'Será criado como grupo global de segurança na pasta de grupos configurada.';
        document.getElementById('modal-ad-create-group').classList.add('active');requestAnimationFrame(()=>document.getElementById('m-ad-group-name').focus());
    }
    function editAdGroup(name){const group=_adGroups.find(g=>g.name.toLocaleUpperCase('pt-BR')===name.toLocaleUpperCase('pt-BR'));if(group)openAdGroupModal(group);}
    function duplicateAdGroup(name){const group=_adGroups.find(g=>g.name.toLocaleUpperCase('pt-BR')===name.toLocaleUpperCase('pt-BR'));if(group)openAdGroupModal(group,true);}
    async function submitAdGroup(){
        const original=document.getElementById('m-ad-group-original').value,name=document.getElementById('m-ad-group-name').value.trim(),description=document.getElementById('m-ad-group-description').value.trim();
        if(!name)return showAdminMessage('error','Informe o nome do grupo.');
        const button=document.getElementById('btn-create-ad-group');button.disabled=true;
        try{const response=await fetch(original?'/api/admin/ad/groups/'+encodeURIComponent(original):'/api/admin/ad/groups',{method:original?'PUT':'POST',headers:hdrs(),body:JSON.stringify({name,description})});const msg=await readResponseMessage(response,original?'Grupo atualizado.':'Grupo criado.');if(!response.ok)return showAdminMessage('error',msg);closeModals();showAdminMessage('success',msg);await loadAd();}
        catch{showAdminMessage('error','Falha de conexão ao salvar o grupo.');}
        finally{button.disabled=false;}
    }
    async function deleteAdGroup(name){
        if(!await askAdminConfirm(`Excluir o grupo ${name} do Active Directory? Esta ação não pode ser desfeita.`))return;
        try{const response=await fetch('/api/admin/ad/groups/'+encodeURIComponent(name),{method:'DELETE',headers:hdrs()});const msg=await readResponseMessage(response,'Grupo excluído.');if(!response.ok)return showAdminMessage('error',msg);showAdminMessage('success',msg);await loadAd();}
        catch{showAdminMessage('error','Falha de conexão ao excluir o grupo.');}
    }
    function openAdComputerModal(computer=null,duplicate=false){
        const editing=computer&&!duplicate;
        document.getElementById('m-ad-computer-original').value=editing?computer.name:'';
        document.getElementById('m-ad-computer-title').textContent=editing?'Editar Computador do Active Directory':duplicate?'Duplicar Computador do Active Directory':'Novo Computador do Active Directory';
        document.getElementById('m-ad-computer-name').value=computer?(duplicate?suggestedAdCopyName(computer.name,15,_adComputers):computer.name):'';
        document.getElementById('m-ad-computer-description').value=computer?.description||'';document.getElementById('m-ad-computer-os').value=computer?.operatingSystem||'Windows 11 Pro';document.getElementById('m-ad-computer-active').checked=computer?computer.isActive!==false:true;
        document.getElementById('m-ad-computer-active-row').classList.toggle('hidden',editing);
        document.getElementById('btn-create-ad-computer').textContent=editing?'Salvar alterações':duplicate?'Duplicar computador':'Criar computador';
        document.getElementById('m-ad-computer-note').textContent=editing?'Nome e atributos serão atualizados no Active Directory. O estado atual da conta será preservado.':'Cria o objeto na pasta de computadores configurada. Uma máquina nova ainda precisa ingressar no domínio pelo próprio Windows.';
        document.getElementById('modal-ad-create-computer').classList.add('active');requestAnimationFrame(()=>document.getElementById('m-ad-computer-name').focus());
    }
    function editAdComputer(name){const computer=_adComputers.find(c=>c.name.toUpperCase()===name.toUpperCase());if(computer)openAdComputerModal(computer);}
    function duplicateAdComputer(name){const computer=_adComputers.find(c=>c.name.toUpperCase()===name.toUpperCase());if(computer)openAdComputerModal(computer,true);}
    async function submitAdComputer(){
        const original=document.getElementById('m-ad-computer-original').value,name=document.getElementById('m-ad-computer-name').value.trim(),description=document.getElementById('m-ad-computer-description').value.trim(),operatingSystem=document.getElementById('m-ad-computer-os').value.trim(),isActive=document.getElementById('m-ad-computer-active').checked;
        if(!name)return showAdminMessage('error','Informe o nome do computador.');
        const button=document.getElementById('btn-create-ad-computer');button.disabled=true;
        try{const response=await fetch(original?'/api/admin/ad/computers/'+encodeURIComponent(original):'/api/admin/ad/computers',{method:original?'PUT':'POST',headers:hdrs(),body:JSON.stringify({name,description,operatingSystem,isActive})});const msg=await readResponseMessage(response,original?'Computador atualizado.':'Computador criado.');if(!response.ok)return showAdminMessage('error',msg);closeModals();showAdminMessage('success',msg);await loadAd();}
        catch{showAdminMessage('error','Falha de conexão ao salvar o computador.');}
        finally{button.disabled=false;}
    }
    async function deleteAdComputer(name){
        if(!await askAdminConfirm(`Excluir o computador ${name} do Active Directory? Esta ação não pode ser desfeita.`))return;
        try{const response=await fetch('/api/admin/ad/computers/'+encodeURIComponent(name),{method:'DELETE',headers:hdrs()});const msg=await readResponseMessage(response,'Computador excluído.');if(!response.ok)return showAdminMessage('error',msg);showAdminMessage('success',msg);await loadAd();}
        catch{showAdminMessage('error','Falha de conexão ao excluir o computador.');}
    }
    function openAdComputerGroupsModal(name){
        const computer=_adComputers.find(item=>item.name.toUpperCase()===name.toUpperCase());
        if(!computer)return;
        document.getElementById('m-ad-computer-groups-name').value=computer.name;
        document.getElementById('m-ad-computer-groups-title').textContent=`Grupos de ${computer.name}`;
        const current=new Set((computer.groups||[]).map(group=>group.toLocaleUpperCase('pt-BR')));
        const list=document.getElementById('m-ad-computer-groups-list');
        list.innerHTML=_adGroups.length?_adGroups.map(group=>`<label class="check-row"><input type="checkbox" value="${esc(group.name)}" ${current.has(group.name.toLocaleUpperCase('pt-BR'))?'checked':''}><span class="check-main"><span class="check-name">${esc(group.name)}</span><span class="check-desc">${esc(group.description||'Sem descrição')}</span></span></label>`).join(''):'<div class="ad-link-empty">Nenhum grupo disponível na pasta configurada.</div>';
        document.getElementById('modal-ad-computer-groups').classList.add('active');
    }
    async function saveAdComputerGroups(){
        const name=document.getElementById('m-ad-computer-groups-name').value;
        const groups=[...document.querySelectorAll('#m-ad-computer-groups-list input:checked')].map(input=>input.value);
        const button=document.getElementById('btn-save-ad-computer-groups');button.disabled=true;
        try{
            const response=await fetch('/api/admin/ad/computers/'+encodeURIComponent(name)+'/groups',{method:'PUT',headers:hdrs(),body:JSON.stringify({groups})});
            const msg=await readResponseMessage(response,'Grupos do computador atualizados.');
            if(!response.ok)return showAdminMessage('error',msg);
            closeModals();showAdminMessage('success',msg);await loadAd();
        }catch{showAdminMessage('error','Falha de conexão ao atualizar os grupos do computador.');}
        finally{button.disabled=false;}
    }

    const AD_SORT_FIELDS = {
        users:['fullName','username','isActive','expiresAt'],
        groups:['name','description'],
        computers:['name','description','operatingSystem','isActive','groups']
    };
    function adSortType(){return currentAdFilter==='groups'?'groups':currentAdFilter==='computers'?'computers':'users';}
    function ensureAdSortForFilter(){
        const fields=AD_SORT_FIELDS[adSortType()];
        if(!fields.includes(_adSortField)){
            _adSortField=fields[0];
            _adSortDirection='asc';
        }
    }
    function sortAdItems(items,fields,fallback){
        const field=fields.includes(_adSortField)?_adSortField:fallback;
        const factor=_adSortDirection==='asc'?1:-1;
        return [...items].sort((a,b)=>{
            let av=a?.[field],bv=b?.[field];
            if(field==='groups'){
                av=Array.isArray(av)?av.join(', '):'';
                bv=Array.isArray(bv)?bv.join(', '):'';
            }
            if(av==null||av==='')return bv==null||bv===''?0:1;
            if(bv==null||bv==='')return -1;
            if(typeof av==='boolean'||typeof bv==='boolean')return (Number(av)-Number(bv))*factor;
            if(field==='expiresAt')return (new Date(av)-new Date(bv))*factor;
            return String(av).localeCompare(String(bv),'pt-BR',{sensitivity:'base',numeric:true})*factor;
        });
    }
    function renderAdUserRow(u){
        const computers=Array.isArray(u.computers)?u.computers:[];
        const computersCsv=computers.join(',');
        const computerText=u.allowAllComputers
            ? '<span class="csp-d003">&#127760; Todos os computadores</span>'
            : (computers.length?computers.map(esc).join(', '):'Nenhum PC');
        const expiresValue=u.expiresAt?u.expiresAt.substring(0,10):'';
        return `<tr>
            <td>${esc(u.username)}</td><td>${esc(u.fullName)}</td>
            <td>${u.isActive?'<span class="csp-d004">Ativo</span>':'<span class="csp-d005">Desativado</span>'}</td>
            <td class="csp-d006"><button class="btn btn-outline csp-d007" data-admin-click="open-ad-access" data-username="${esc(u.username)}" data-computers="${esc(computersCsv)}" data-allow-all="${u.allowAllComputers}">&#128187; Gerenciar Acessos</button><div class="csp-d008">${computerText}</div></td>
            <td><div class="date-tools"><label class="inline-check"><input type="checkbox" id="never_${u.username}" data-admin-change="toggle-ad-never" data-username="${esc(u.username)}" ${expiresValue?'':'checked'}> Nunca</label><input type="date" id="exp_${u.username}" value="${expiresValue}" ${expiresValue?'':'disabled'}><button class="btn btn-outline csp-d007" data-admin-click="set-ad-expire" data-username="${esc(u.username)}">&#10004;</button></div></td>
            <td><details class="action-details"><summary class="btn btn-outline">Mais ações</summary><div class="action-menu-panel">
                <button class="btn btn-outline csp-d009" data-admin-click="open-ad-edit" data-username="${esc(u.username)}">&#9998; Editar</button>
                <button class="btn btn-outline csp-d010" data-admin-click="open-ad-password" data-username="${esc(u.username)}">&#128274; Senha</button>
                <button class="btn btn-outline csp-d011" data-admin-click="open-duplicate" data-username="${esc(u.username)}" data-full-name="${esc(u.fullName)}">&#128203; Duplicar</button>
                ${u.ouPath==='USUARIOS'?`<button class="btn btn-outline csp-d012" data-admin-click="move-ou" data-username="${esc(u.username)}" data-archive="true">Arquivar</button>`:`<button class="btn btn-outline csp-d013" data-admin-click="move-ou" data-username="${esc(u.username)}" data-archive="false">${u.ouPath==='USUARIOS_WEBSITE'?'Mover para ativos':'Reativar'}</button>`}
                <button class="btn btn-outline csp-d014" data-admin-click="delete-ad-user" data-username="${esc(u.username)}">Excluir</button>
            </div></details></td>
        </tr>`;
    }
    function renderAdCollections(){
        if(!document.getElementById('ad-users-body'))return;
        const userFields=['fullName','username','isActive','expiresAt'];
        const users=sortAdItems(_adUsers,userFields,'fullName');
        document.getElementById('ad-users-body').innerHTML=users.filter(x=>x.ouPath==='USUARIOS').map(renderAdUserRow).join('')||'<tr><td colspan="6" class="empty">Nenhum usuário ativo encontrado.</td></tr>';
        document.getElementById('ad-website-body').innerHTML=users.filter(x=>x.ouPath==='USUARIOS_WEBSITE').map(renderAdUserRow).join('')||'<tr><td colspan="6" class="empty">Nenhum usuário de website encontrado.</td></tr>';
        document.getElementById('ad-expired-body').innerHTML=users.filter(x=>x.ouPath==='USUARIOS_EXPIRADOS').map(renderAdUserRow).join('')||'<tr><td colspan="6" class="empty">Nenhum usuário expirado encontrado.</td></tr>';
        const computers=sortAdItems(_adComputers,['name','description','operatingSystem','isActive','groups'],'description');
        document.getElementById('ad-computers-body').innerHTML=computers.map(c=>`<tr><td>${esc(c.name)}</td><td>${esc(c.description||'-')}</td><td>${esc(c.operatingSystem||'-')}</td><td>${c.isActive!==false?'<span class="badge b-ok">Ativo</span>':'<span class="badge b-muted">Inativo</span>'}</td><td><div class="computer-groups">${(c.groups||[]).length?(c.groups||[]).map(group=>`<span class="badge b-accent">${esc(group)}</span>`).join(''):'<span class="muted">Nenhum</span>'}<button class="btn btn-outline ad-table-action" data-admin-click="open-ad-computer-groups" data-computer="${esc(c.name)}">Gerenciar</button></div></td><td><button class="btn btn-outline ad-table-action" data-admin-click="edit-ad-computer" data-computer="${esc(c.name)}">Editar</button></td><td><button class="btn btn-outline ad-table-action" data-admin-click="duplicate-ad-computer" data-computer="${esc(c.name)}">Duplicar</button></td><td><button class="btn btn-outline ad-table-action danger-action" data-admin-click="delete-ad-computer" data-computer="${esc(c.name)}">Excluir</button></td></tr>`).join('')||'<tr><td colspan="8" class="empty">Nenhum computador encontrado.</td></tr>';
        const groups=sortAdItems(_adGroups,['name','description'],'name');
        document.getElementById('ad-groups-body').innerHTML=groups.map(g=>`<tr><td>${esc(g.name)}</td><td>${esc(g.description||'-')}</td><td><button class="btn btn-outline ad-table-action" data-admin-click="edit-ad-group" data-group="${esc(g.name)}">Editar</button></td><td><button class="btn btn-outline ad-table-action" data-admin-click="duplicate-ad-group" data-group="${esc(g.name)}">Duplicar</button></td><td><button class="btn btn-outline ad-table-action danger-action" data-admin-click="delete-ad-group" data-group="${esc(g.name)}">Excluir</button></td></tr>`).join('')||'<tr><td colspan="5" class="empty">Nenhum grupo encontrado.</td></tr>';
    }

    async function loadAd() {
        const sts = document.getElementById('ad-status');
        const cont = document.getElementById('ad-content');
        
        sts.innerHTML = 'Conectando ao servidor 172.31.2.3...';
        const r = await apiFetch('/api/admin/ad/status');
        if(!r || !r.online) {
            sts.innerHTML = '<span class="csp-d005">Desconectado</span>';
            document.getElementById('ad-actions').classList.add('hidden');
            cont.innerHTML = `<div class="offline-banner">
                <span class="csp-d015">&#128268;</span>
                <h3 class="csp-d016">Servidor Active Directory Desligado</h3>
                <p class="csp-d017">O servidor Windows Server (172.31.2.3) n&atilde;o respondeu no porto 389. Esta vis&atilde;o est&aacute; restrita para economia de energia.</p>
                <button class="btn btn-outline csp-d018" data-admin-click="load-ad">&#128260; Tentar Novamente</button>
            </div>`;
            cont.classList.remove('hidden');
            return;
        }

        sts.innerHTML = '<span class="csp-d004">Online (172.31.2.3)</span>';
        document.getElementById('ad-actions').classList.remove('hidden');
        syncAdCreationActions();
        
        // Restore layout if it was overwritten by offline banner
        if(cont.innerHTML.includes('offline-banner')) {
            currentAdFilter = 'users';
            cont.innerHTML = `
                <div class="filter-bar" id="ad-fbar">
                    <button class="fb active" data-admin-click="set-ad-filter" data-filter="users">Usu&aacute;rios Ativos</button>
                    <button class="fb" data-admin-click="set-ad-filter" data-filter="website">Website Users</button>
                    <button class="fb" data-admin-click="set-ad-filter" data-filter="expired">Usu&aacute;rios Expirados</button>
                    <button class="fb" data-admin-click="set-ad-filter" data-filter="groups">Grupos</button>
                    <button class="fb" data-admin-click="set-ad-filter" data-filter="computers">Computadores</button>
                </div>
                <div class="tbl-card" id="ad-users-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Usu&aacute;rio</th><th>Nome Completo</th><th>Status</th><th>Acessos (Computadores e Grupos)</th><th>Vencimento</th><th>A&ccedil;&otilde;es</th></tr></thead><tbody id="ad-users-body"></tbody></table></div>
                </div>
                <div class="tbl-card hidden" id="ad-website-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Usu&aacute;rio</th><th>Nome Completo</th><th>Status</th><th>Acessos (Computadores e Grupos)</th><th>Vencimento</th><th>A&ccedil;&otilde;es</th></tr></thead><tbody id="ad-website-body"></tbody></table></div>
                </div>
                <div class="tbl-card hidden" id="ad-expired-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Usu&aacute;rio</th><th>Nome Completo</th><th>Status</th><th>Acessos (Computadores e Grupos)</th><th>Vencimento</th><th>A&ccedil;&otilde;es</th></tr></thead><tbody id="ad-expired-body"></tbody></table></div>
                </div>
                <div class="tbl-card hidden" id="ad-groups-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Grupo</th><th>Descri&ccedil;&atilde;o</th><th>Editar</th><th>Duplicar</th><th>Excluir</th></tr></thead><tbody id="ad-groups-body"></tbody></table></div>
                </div>
                <div class="tbl-card hidden" id="ad-computers-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Computador</th><th>Descri&ccedil;&atilde;o</th><th>Sistema Operacional</th><th>Status</th><th>Grupos</th><th>Editar</th><th>Duplicar</th><th>Excluir</th></tr></thead><tbody id="ad-computers-body"></tbody></table></div>
                </div>
            `;
        }
        
        cont.classList.remove('hidden');

        // Load computers first because the access modal uses this list.
        const cres = await apiFetch('/api/admin/ad/computers');
        _adComputers = Array.isArray(cres) ? cres : [];

        // Load users
        const ures = await apiFetch('/api/admin/ad/users');
        _adUsers = Array.isArray(ures) ? ures : [];
        _adLinkOptionsLoaded = false;

        // Load groups
        const gres = await apiFetch('/api/admin/ad/groups');
        _adGroups = Array.isArray(gres) ? gres : [];
        ensureAdSortForFilter();
        renderAdCollections();
        enhanceSortableHeaders();
        updateSortableHeaderState();

    }

    function normalizeComputerName(name) {
        return String(name || '').trim().toUpperCase();
    }

    function openAdAccessModal(u, pcsStr, allowAll) {
        _adAccessUser = u;
        _adAllowAllComputers = allowAll === true;
        _adSelectedComputers = new Set(String(pcsStr || '').split(',').map(normalizeComputerName).filter(Boolean));
        _adComputerGroups = {};
        _adPendingGroupSelection = null;
        _adAccessSortField = 'computer';
        _adAccessSortDirection = 'asc';
        document.getElementById('m-ad-access-title').textContent = 'Acessos de ' + u;
        document.getElementById('ad-computer-search').value = '';
        renderAdComputerChecks();
        document.getElementById('modal-ad-access').classList.add('active');
    }

    function renderAdComputerChecks() {
        const holder = document.getElementById('ad-computer-checks');
        const q = normalizeComputerName(document.getElementById('ad-computer-search')?.value || '');
        const list = _adComputers
            .filter(c => c.isActive !== false || _adSelectedComputers.has(normalizeComputerName(c.name)))
            .filter(c => {
                if(!q) return true;
                return normalizeComputerName(c.name).includes(q)
                    || normalizeComputerName(c.description).includes(q)
                    || normalizeComputerName(c.operatingSystem).includes(q);
            })
            .sort((a,b) => {
                const factor = _adAccessSortDirection === 'asc' ? 1 : -1;
                const av = _adAccessSortField === 'status'
                    ? (a.isActive !== false ? 'Ativo' : 'Inativo')
                    : String(a.description || a.name || '');
                const bv = _adAccessSortField === 'status'
                    ? (b.isActive !== false ? 'Ativo' : 'Inativo')
                    : String(b.description || b.name || '');
                const compared = av.localeCompare(bv, 'pt-BR', {sensitivity:'base', numeric:true});
                return compared * factor || String(a.name||'').localeCompare(String(b.name||''), 'pt-BR', {sensitivity:'base',numeric:true});
            });

        if(!list.length) {
            holder.innerHTML = '<div class="empty">Nenhum computador ativo encontrado.</div>';
            return;
        }

        let html = `<label class="check-row csp-d019">
            <input type="checkbox" id="ad-check-all" data-admin-change="toggle-ad-all" ${ _adAllowAllComputers ? 'checked' : '' }>
            <span class="csp-d020">&#127760; Liberar todos os computadores</span>
        </label>`;

        const computerArrow = _adAccessSortField === 'computer' ? (_adAccessSortDirection === 'asc' ? '↑' : '↓') : '';
        const statusArrow = _adAccessSortField === 'status' ? (_adAccessSortDirection === 'asc' ? '↑' : '↓') : '';
        html += `<div class="check-list-header" role="row"><span aria-hidden="true"></span><button type="button" data-admin-click="sort-ad-access" data-field="computer">Computadores <span class="sort-indicator">${computerArrow}</span></button><button type="button" data-admin-click="sort-ad-access" data-field="status">Status <span class="sort-indicator">${statusArrow}</span></button></div>`;

        html += list.map(c => {
            const name = normalizeComputerName(c.name);
            const checked = _adSelectedComputers.has(name) ? 'checked' : '';
            const desc = c.description || c.operatingSystem || 'Sem descricao';
            return `<label class="check-row">
                <input type="checkbox" value="${esc(name)}" ${checked} data-admin-change="toggle-ad-computer" class="ad-comp-cb">
                <div class="check-main"><div class="check-name">${esc(name)}</div><div class="check-desc">${esc(desc)}</div></div>
                ${c.isActive!==false?'<span class="badge b-ok">Ativo</span>':'<span class="badge b-muted">Inativo</span>'}
            </label>`;
        }).join('');
        
        holder.innerHTML = html;
        toggleAdAllComputers(); // Update disabled state initially
    }

    function sortAdAccessComputers(field) {
        if(_adAccessSortField === field) _adAccessSortDirection = _adAccessSortDirection === 'asc' ? 'desc' : 'asc';
        else { _adAccessSortField = field; _adAccessSortDirection = 'asc'; }
        renderAdComputerChecks();
    }

    function toggleAdAllComputers() {
        const isAll = document.getElementById('ad-check-all').checked;
        const cbs = document.querySelectorAll('.ad-comp-cb');
        cbs.forEach(cb => {
            cb.disabled = isAll;
            if(isAll) cb.checked = false;
        });
        if(isAll) _adSelectedComputers.clear();
    }

    function toggleAdComputer(checkbox) {
        const name = normalizeComputerName(checkbox.value);
        if(!name) return;
        if(checkbox.checked) _adSelectedComputers.add(name);
        else _adSelectedComputers.delete(name);
    }

    async function saveAdAccesses() {
        const isAll = document.getElementById('ad-check-all').checked;
        const pcs = isAll ? '' : Array.from(_adSelectedComputers).sort().join(',');
        const r = await fetch('/api/admin/ad/users/'+_adAccessUser+'/computers', {
            method: 'PUT',
            headers: hdrs(),
            body: JSON.stringify({
                ComputersStr: pcs,
                AllowAllComputers: isAll,
                ComputerGroups: _adComputerGroups
            })
        });
        let data = {};
        try { data = await r.json(); } catch {}

        if(r.status === 409 && data.requiresGroupSelection) {
            openManualGroupSelection(data);
            return;
        }

        const msg = data.msg || data.erro || 'Acessos atualizados.';
        if(r.ok) {
            showAdminMessage('success', msg);
            closeModals();
            loadAd();
        } else {
            document.getElementById('modal-ad-access').classList.add('active');
            showAdminMessage('error', msg);
        }
    }

    async function openManualGroupSelection(data) {
        _adPendingGroupSelection = data;
        if(!_adGroups.length) {
            const groups = await apiFetch('/api/admin/ad/groups');
            _adGroups = Array.isArray(groups) ? groups : [];
        }

        const action = data.operation === 'remove' ? 'remover' : 'adicionar';
        const suggestion = data.suggestedGroup
            ? ` O grupo automático esperado era ${data.suggestedGroup}.`
            : '';
        document.getElementById('ad-group-select-note').textContent =
            `Selecione o grupo que deve ser usado para ${action} o acesso do computador ${data.computer}.${suggestion} A escolha também será salva como grupo do computador no Active Directory.`;

        const select = document.getElementById('ad-manual-group');
        select.innerHTML = '<option value="">Selecione um grupo</option>' +
            _adGroups.map(g => `<option value="${esc(g.name)}">${esc(g.name)}${g.description ? ' - ' + esc(g.description) : ''}</option>`).join('');

        document.getElementById('modal-ad-access').classList.remove('active');
        document.getElementById('modal-ad-group-select').classList.add('active');
    }

    function cancelManualGroupSelection() {
        document.getElementById('modal-ad-group-select').classList.remove('active');
        document.getElementById('modal-ad-access').classList.add('active');
    }

    function confirmManualGroupSelection() {
        const groupName = document.getElementById('ad-manual-group').value;
        if(!groupName || !_adPendingGroupSelection) {
            showAdminMessage('error', 'Selecione um grupo.');
            return;
        }

        _adComputerGroups[_adPendingGroupSelection.computer] = groupName;
        document.getElementById('modal-ad-group-select').classList.remove('active');
        saveAdAccesses();
    }

    async function moveOu(u, toExpired) {
        if(!await askAdminConfirm('Mover ' + u + ' para a pasta ' + (toExpired?'Expirados':'Ativos') + '?')) return;
        const r = await fetch('/api/admin/ad/users/'+u+'/ou', { method: 'PUT', headers: hdrs(), body: JSON.stringify({ToExpired: toExpired}) });
        const msg = await readResponseMessage(r, 'Usuario movido.');
        if(r.ok) { showAdminMessage('success', msg); loadAd(); } else showAdminMessage('error', msg);
    }

    async function submitAdUser() {
        const body = { 
            Username: document.getElementById('m-ad-username').value.trim(), 
            FullName: document.getElementById('m-ad-fullname').value.trim(), 
            Whatsapp: document.getElementById('m-ad-whatsapp').value.trim(),
            Password: document.getElementById('m-ad-password').value 
        };
        const r = await fetch('/api/admin/ad/users', { method: 'POST', headers: hdrs(), body: JSON.stringify(body) });
        const msg = await readResponseMessage(r, 'Usuario AD criado.');
        if(r.ok) { showAdminMessage('success', msg); closeModals(); loadAd(); } else showAdminMessage('error', msg);
    }

    async function submitAdEdit() {
        const username = document.getElementById('m-ad-edit-username').value;
        const body = { 
            FullName: document.getElementById('m-ad-edit-fullname').value.trim(), 
            Email: document.getElementById('m-ad-edit-email').value.trim(),
            Whatsapp: document.getElementById('m-ad-edit-whatsapp').value.trim(),
            Password: document.getElementById('m-ad-edit-password').value,
            IsActive: document.getElementById('m-ad-edit-active').checked,
            PasswordNeverExpires: document.getElementById('m-ad-edit-never-expires').checked
        };
        const r = await fetch('/api/admin/ad/users/' + encodeURIComponent(username), { method: 'PUT', headers: hdrs(), body: JSON.stringify(body) });
        const msg = await readResponseMessage(r, 'Usuario atualizado.');
        if(r.ok) { showAdminMessage('success', msg); closeModals(); loadAd(); } else showAdminMessage('error', msg);
    }
    
    async function deleteAdUser(u) {
        if(!await askAdminConfirm('Excluir definitivamente ' + u + ' do Active Directory?')) return;
        const r = await fetch('/api/admin/ad/users/'+u, { method: 'DELETE', headers: hdrs() });
        const msg = await readResponseMessage(r, 'Usuario removido.');
        if(r.ok) { showAdminMessage('success', msg); loadAd(); } else showAdminMessage('error', msg);
    }

    function toggleAdNever(u) {
        const never = document.getElementById('never_'+u);
        const input = document.getElementById('exp_'+u);
        if(!never || !input) return;
        input.disabled = never.checked;
        if(never.checked) {
            input.value = '';
        } else if (!input.value) {
            // Pegar a data local em vez de UTC
            const tzoffset = (new Date()).getTimezoneOffset() * 60000; 
            const localISOTime = (new Date(Date.now() - tzoffset)).toISOString().slice(0, -1);
            input.value = localISOTime.split('T')[0];
        }
    }

    async function setAdExpire(u) {
        const never = document.getElementById('never_'+u)?.checked;
        const val = document.getElementById('exp_'+u)?.value || '';
        if(!never && !val) {
            showAdminMessage('error', 'Informe uma data ou marque Nunca.');
            return;
        }
        const body = { ExpiresAt: never ? null : val };
        const r = await fetch('/api/admin/ad/users/'+u+'/expiration', { method: 'PUT', headers: hdrs(), body: JSON.stringify(body) });
        const msg = await readResponseMessage(r, 'Vencimento atualizado.');
        if(r.ok) showAdminMessage('success', msg); else showAdminMessage('error', msg);
    }

const ADMIN_ROUTES={dashboard:'/admin/dashboard',financeiro:'/admin/financeiro',crm:'/admin/crm',trials:'/admin/testes-gratis',pedidos:'/admin/pedidos',usuarios:'/admin/usuarios',ad:'/admin/active-directory',notificacoes:'/admin/notificacoes',logs:'/admin/logs'};
const S={csrfToken:null,user:null,loginChallengeId:null,twoFactorSetup:false,view:document.body?.dataset.view||'dashboard',ordFilter:'all',ordPage:1,ordSort:'createdAt',ordSortDir:'desc',usrPage:1,usrSearch:'',usrSort:'createdAt',usrSortDir:'desc',trialPage:1,trialFilter:'all',trialSearch:'',trialSort:'lastRequestedAt',trialSortDir:'desc',chart:null,statusChart:null,dashData:null,dashPeriod:localStorage.getItem('premierAdminDashPeriod')||'month',waTemplates:[],waSelected:null,waOriginalBody:'',waHistory:[],waHistoryIndex:-1,waApplyingHistory:false};
const API='/api/admin';
let chartLoaderPromise=null;
let adminNavigationController=null;
let adminNavigationRequest=0;

function ensureChartJs(){
  if(window.Chart)return Promise.resolve(true);
  if(chartLoaderPromise)return chartLoaderPromise;
  chartLoaderPromise=new Promise(resolve=>{
    const script=document.createElement('script');script.src='/admin/assets/vendor/chart.umd.min.js';script.async=true;
    script.addEventListener('load',()=>resolve(Boolean(window.Chart)),{once:true});
    script.addEventListener('error',()=>{showAdminMessage('error','Nao foi possivel carregar os graficos.');resolve(false);},{once:true});
    document.head.appendChild(script);
  });
  return chartLoaderPromise;
}

async function init(){
  const loginError=document.getElementById('lerr');if(loginError){loginError.setAttribute('role','alert');loginError.setAttribute('aria-live','assertive');}
  document.querySelectorAll('label').forEach(label=>{if(label.htmlFor)return;const control=label.parentElement?.querySelector('input,select,textarea');if(control?.id)label.htmlFor=control.id;});
  document.querySelectorAll('button[title]').forEach(button=>{if(!button.getAttribute('aria-label'))button.setAttribute('aria-label',button.title);});
  setupResponsiveTables();
  localStorage.removeItem('premierAdmin');
  try{
    const response=await fetch(API+'/session',{cache:'no-store'});
    if(response.ok){const data=await response.json();S.csrfToken=data.csrfToken;S.user=data.user;await adminPartialsPromise;showApp();return;}
  }catch(e){}
  showLogin();
}
function enhanceResponsiveTables(root=document){
  root.querySelectorAll?.('.tbl-wrap > table').forEach(table=>{
    table.classList.add('responsive-table');
    const wrapper=table.parentElement;
    if(wrapper&&!wrapper.hasAttribute('tabindex'))wrapper.tabIndex=0;
    const labels=[...table.querySelectorAll('thead th')].map(th=>th.querySelector('.th-inner > span:first-child')?.textContent.trim()||th.textContent.trim());
    table.querySelectorAll('tbody tr').forEach(row=>{
      [...row.children].forEach((cell,index)=>{
        if(cell.colSpan>1)return;
        cell.dataset.label=labels[index]||'';
      });
    });
  });
  enhanceSortableHeaders(root);
}
const SORTABLE_TABLES={
  'orders-body':{type:'orders',fields:['customer','whatsapp','server','period','computers','totalPrice','asaasPaymentId','status','delivered','expiresAt','createdAt','canceledAt',null]},
  'users-body':{type:'users',fields:['name','whatsapp','isActive','adUsername','totalOrders','totalSpent','activeLicenses','emailConfirmed','createdAt',null]},
  'trials-body':{type:'trials',fields:['name','whatsapp','status','createdAt','firstRequestedAt','lastRequestedAt','releasedAt','usedAt',null]},
  'ad-users-body':{type:'ad',fields:['username','fullName','isActive',null,'expiresAt',null]},
  'ad-website-body':{type:'ad',fields:['username','fullName','isActive',null,'expiresAt',null]},
  'ad-expired-body':{type:'ad',fields:['username','fullName','isActive',null,'expiresAt',null]},
  'ad-groups-body':{type:'ad',fields:['name','description',null,null,null]},
  'ad-computers-body':{type:'ad',fields:['name','description','operatingSystem','isActive','groups',null,null,null]}
};
function enhanceSortableHeaders(root=document){
  Object.entries(SORTABLE_TABLES).forEach(([bodyId,config])=>{
    const body=(root.getElementById?.(bodyId)||document.getElementById(bodyId));
    const table=body?.closest('table');if(!table)return;
    [...table.querySelectorAll('thead th')].forEach((th,index)=>{
      const field=config.fields[index];if(!field||th.querySelector('.column-sort-button'))return;
      const label=th.textContent.trim();
      th.textContent='';th.classList.add('sortable-th');
      const inner=document.createElement('span');inner.className='th-inner';
      const text=document.createElement('span');text.textContent=label;inner.appendChild(text);
      const button=document.createElement('button');button.type='button';button.className='column-sort-button';
      button.dataset.sortType=config.type;button.dataset.sortField=field;
      button.title=`Ordenar por ${label}`;button.setAttribute('aria-label',button.title);
      const indicator=document.createElement('span');indicator.className='sort-indicator';indicator.setAttribute('aria-hidden','true');
      button.appendChild(indicator);button.addEventListener('click',event=>{event.stopPropagation();applyColumnSort(config.type,field);});
      th.addEventListener('click',()=>applyColumnSort(config.type,field));
      inner.appendChild(button);th.appendChild(inner);
    });
  });
  updateSortableHeaderState();
}
function currentSortState(type){
  if(type==='orders')return{field:S.ordSort,direction:S.ordSortDir};
  if(type==='users')return{field:S.usrSort,direction:S.usrSortDir};
  if(type==='trials')return{field:S.trialSort,direction:S.trialSortDir};
  return{field:_adSortField,direction:_adSortDirection};
}
function updateSortableHeaderState(){
  document.querySelectorAll('.column-sort-button').forEach(button=>{
    const state=currentSortState(button.dataset.sortType);
    const active=state.field===button.dataset.sortField;
    button.classList.toggle('active',active);button.setAttribute('aria-pressed',String(active));
    const indicator=button.querySelector('.sort-indicator');if(indicator)indicator.textContent=active?(state.direction==='asc'?'↑':'↓'):'';
    const th=button.closest('th');if(active)th?.setAttribute('aria-sort',state.direction==='asc'?'ascending':'descending');
    else th?.removeAttribute('aria-sort');
  });
}
function applyColumnSort(type,field){
  const state=currentSortState(type);const direction=state.field===field&&state.direction==='asc'?'desc':'asc';
  if(type==='orders'){S.ordSort=field;S.ordSortDir=direction;S.ordPage=1;updateSortableHeaderState();loadOrders();return;}
  if(type==='users'){S.usrSort=field;S.usrSortDir=direction;S.usrPage=1;updateSortableHeaderState();loadUsers();return;}
  if(type==='trials'){S.trialSort=field;S.trialSortDir=direction;S.trialPage=1;updateSortableHeaderState();loadFreeTrials();return;}
  _adSortField=field;_adSortDirection=direction;renderAdCollections();updateSortableHeaderState();
}
function setupResponsiveTables(){
  enhanceResponsiveTables();
  if(window._adminResponsiveObserver)return;
  let scheduled=false;
  window._adminResponsiveObserver=new MutationObserver(()=>{
    if(scheduled)return;
    scheduled=true;
    requestAnimationFrame(()=>{scheduled=false;enhanceResponsiveTables();});
  });
  window._adminResponsiveObserver.observe(document.body,{childList:true,subtree:true});
}
function completeAdminSessionGate(){document.body.classList.remove('admin-session-pending');document.body.removeAttribute('aria-busy');}
function showLogin(){completeAdminSessionGate();document.getElementById('login-screen').style.display='flex';document.getElementById('app').style.display='none';}
function showApp(){
  completeAdminSessionGate();
  document.getElementById('login-screen').style.display='none';document.getElementById('app').style.display='flex';
  setupAdminMobileNavigation();
  const n=S.user?.name||'Admin';document.getElementById('sname').textContent=n;document.getElementById('savatar').textContent=n[0].toUpperCase();
  setupCurrentView();
  setupAdminNavigation();
  loadCurrentView();
  setupMaintenanceControls();
  resumeMaintenanceState();
}
function setupAdminMobileNavigation(){
  const header=document.querySelector('#main > header');
  if(!header)return;
  let button=header.querySelector('.mobile-nav-toggle');
  if(!button){button=document.createElement('button');button.type='button';button.className='mobile-nav-toggle';button.setAttribute('aria-label','Abrir menu administrativo');button.setAttribute('aria-controls','sidebar');button.setAttribute('aria-expanded','false');button.textContent='☰';header.prepend(button);}
  if(button.dataset.wired==='true')return;button.dataset.wired='true';
  const backdrop=document.createElement('div');backdrop.className='sidebar-backdrop';document.body.appendChild(backdrop);
  const close=()=>{document.getElementById('sidebar')?.classList.remove('mobile-open');backdrop.classList.remove('active');button.setAttribute('aria-expanded','false');};
  button.addEventListener('click',()=>{const sidebar=document.getElementById('sidebar');const open=!sidebar.classList.contains('mobile-open');sidebar.classList.toggle('mobile-open',open);backdrop.classList.toggle('active',open);button.setAttribute('aria-expanded',String(open));});
  backdrop.addEventListener('click',close);document.querySelectorAll('.ni').forEach(link=>link.addEventListener('click',close));
}

const MAINTENANCE_JOB_KEY='premier_admin_maintenance_job';
const MAINTENANCE_RESULT_KEY='premier_admin_maintenance_result';
let maintenancePollTimer=null;

function setupMaintenanceControls(){
  const footer=document.querySelector('.sfooter');
  if(footer&&!document.getElementById('maintenance-menu')){
    const menu=document.createElement('details');
    menu.id='maintenance-menu';
    menu.className='maintenance-menu';
    menu.innerHTML=`<summary><span aria-hidden="true">&#9881;</span><span>Manuten&ccedil;&atilde;o</span><span class="maintenance-chevron" aria-hidden="true">&#9662;</span></summary><div class="maintenance-actions"><button type="button" data-admin-click="start-maintenance" data-operation="publish">Compilar e reiniciar website</button><button type="button" data-admin-click="start-maintenance" data-operation="restart">Reiniciar servi&ccedil;o</button></div>`;
    footer.prepend(menu);
  }
  if(!document.getElementById('maintenance-blocker')){
    const blocker=document.createElement('div');
    blocker.id='maintenance-blocker';
    blocker.className='maintenance-blocker';
    blocker.setAttribute('role','alertdialog');
    blocker.setAttribute('aria-modal','true');
    blocker.innerHTML='<div class="maintenance-progress-card"><div class="maintenance-spinner" aria-hidden="true"></div><h2>Manuten&ccedil;&atilde;o em andamento</h2><p id="maintenance-progress-message">Preparando manuten&ccedil;&atilde;o...</p><small>O painel ser&aacute; liberado automaticamente quando a opera&ccedil;&atilde;o terminar.</small></div>';
    document.body.appendChild(blocker);
  }
  if(!document.getElementById('maintenance-result')){
    const modal=document.createElement('div');
    modal.id='maintenance-result';
    modal.className='modal-overlay maintenance-result';
    modal.innerHTML='<div class="modal-box maintenance-result-box"><div id="maintenance-result-icon" class="maintenance-result-icon" aria-hidden="true"></div><div id="maintenance-result-title" class="modal-title"></div><div class="modal-body"><p id="maintenance-result-message" class="modal-note"></p><pre id="maintenance-result-log" class="maintenance-result-log hidden"></pre></div><div class="modal-footer"><a id="maintenance-result-logs" class="btn btn-outline hidden" href="/admin/logs">Conferir logs</a><button type="button" class="btn btn-primary" data-admin-click="close-maintenance-result">Fechar</button></div></div>';
    document.body.appendChild(modal);
  }
}

async function startAdminMaintenance(operation){
  const publish=operation==='publish';
  const confirmed=await askAdminConfirm(
    publish
      ? 'Compilar a aplicação em Release e reiniciar o website? O site ficará indisponível por alguns instantes.'
      : 'Reiniciar o serviço premierapi agora? O site ficará indisponível por alguns instantes.',
    {title:'Confirmar manutenção',confirmText:publish?'Compilar e reiniciar':'Reiniciar'});
  if(!confirmed)return;
  showMaintenanceBlocker(publish?'Solicitando compilação...':'Solicitando reinício...');
  try{
    const response=await fetch(`${API}/maintenance/${operation}`,{method:'POST',headers:hdrs()});
    const data=await response.json().catch(()=>null);
    const status=data?.status;
    if(!response.ok||!status?.jobId)throw new Error(data?.erro||'Não foi possível iniciar a manutenção.');
    sessionStorage.setItem(MAINTENANCE_JOB_KEY,status.jobId);
    pollMaintenanceStatus(status.jobId);
  }catch(error){
    hideMaintenanceBlocker();
    showMaintenanceResult({phase:'failed',message:error.message||'Não foi possível iniciar a manutenção.'});
  }
}

function resumeMaintenanceState(){
  setupMaintenanceControls();
  const storedResult=sessionStorage.getItem(MAINTENANCE_RESULT_KEY);
  if(storedResult){
    sessionStorage.removeItem(MAINTENANCE_RESULT_KEY);
    try{showMaintenanceResult(JSON.parse(storedResult));}catch{}
    return;
  }
  const jobId=sessionStorage.getItem(MAINTENANCE_JOB_KEY);
  if(jobId){showMaintenanceBlocker('Retomando acompanhamento da manutenção...');pollMaintenanceStatus(jobId);}
}

async function pollMaintenanceStatus(jobId){
  clearTimeout(maintenancePollTimer);
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),4500);
  try{
    const response=await fetch(`${API}/maintenance/${encodeURIComponent(jobId)}`,{headers:hdrs(),signal:controller.signal,cache:'no-store'});
    const status=await response.json().catch(()=>null);
    if(response.ok&&status){
      showMaintenanceBlocker(status.message||maintenancePhaseMessage(status.phase));
      if(status.phase==='success'||status.phase==='warning'){
        sessionStorage.removeItem(MAINTENANCE_JOB_KEY);
        sessionStorage.setItem(MAINTENANCE_RESULT_KEY,JSON.stringify(status));
        window.location.reload();
        return;
      }
      if(status.phase==='failed'){
        sessionStorage.removeItem(MAINTENANCE_JOB_KEY);
        hideMaintenanceBlocker();
        showMaintenanceResult(status);
        return;
      }
    }else if(response.status===401){
      sessionStorage.removeItem(MAINTENANCE_JOB_KEY);
      hideMaintenanceBlocker();
      showMaintenanceResult({phase:'failed',message:'A sessão administrativa expirou durante a manutenção.'});
      return;
    }
  }catch{
    showMaintenanceBlocker('Aguardando o website voltar a responder...');
  }finally{
    clearTimeout(timeout);
  }
  maintenancePollTimer=setTimeout(()=>pollMaintenanceStatus(jobId),2000);
}

function maintenancePhaseMessage(phase){
  return {queued:'Preparando manutenção...',building:'Compilando a aplicação...',restarting:'Reiniciando o serviço...',waiting:'Aguardando o website voltar a responder...'}[phase]||'Manutenção em andamento...';
}

function showMaintenanceBlocker(message){
  setupMaintenanceControls();
  document.getElementById('maintenance-progress-message').textContent=message;
  document.getElementById('maintenance-blocker').classList.add('active');
  document.body.classList.add('maintenance-locked');
}

function hideMaintenanceBlocker(){
  document.getElementById('maintenance-blocker')?.classList.remove('active');
  document.body.classList.remove('maintenance-locked');
}

function showMaintenanceResult(status){
  setupMaintenanceControls();
  const warning=status.phase==='warning';
  const success=status.phase==='success';
  const modal=document.getElementById('maintenance-result');
  modal.classList.toggle('is-success',success);
  modal.classList.toggle('is-warning',warning);
  modal.classList.toggle('is-error',!success&&!warning);
  document.getElementById('maintenance-result-icon').textContent=success?'✓':warning?'⚠':'✕';
  document.getElementById('maintenance-result-title').textContent=success?'Website recarregado':warning?'Recarregado com avisos':'Falha na manutenção';
  document.getElementById('maintenance-result-message').textContent=status.message||'A operação foi concluída.';
  const log=document.getElementById('maintenance-result-log');
  log.textContent=status.logTail||'';
  log.classList.toggle('hidden',!status.logTail);
  document.getElementById('maintenance-result-logs').classList.toggle('hidden',success);
  modal.classList.add('active');
  setTimeout(()=>modal.querySelector('button')?.focus(),0);
}

function closeMaintenanceResult(){
  document.getElementById('maintenance-result')?.classList.remove('active');
}

async function doLogin(){
  const pass=document.getElementById('lpass').value;
  const btn=document.getElementById('lbtn');const err=document.getElementById('lerr');err.style.display='none';
  if(!pass){showErr('Preencha o token.');return;}
  let tToken='';if(window.turnstile)tToken=window.turnstile.getResponse();
  btn.disabled=true;btn.innerHTML='<div class="spinner"></div> Entrando...';
  try{
    const r=await fetch('/api/admin/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Token:pass,"cf-turnstile-response":tToken})});
    const d=await r.json();
    if(!r.ok){if(window.turnstile)window.turnstile.reset();showErr(d.erro||'Erro ao fazer login.');return;}
    document.getElementById('lpass').value='';
    if(d.requiresTwoFactor||d.requiresTwoFactorSetup){showTwoFactorStep(d);return;}
    showErr('Resposta de autenticação inválida.');
  }catch(e){if(window.turnstile)window.turnstile.reset();showErr('Erro de conexao. Tente novamente.');}
  finally{btn.disabled=false;if(!S.loginChallengeId)btn.innerHTML='Entrar no Painel';}
  function showErr(m){err.textContent=m;err.style.display='block';}
}

function showTwoFactorStep(data){
  S.loginChallengeId=data.challengeId;S.twoFactorSetup=!!data.requiresTwoFactorSetup;
  const card=document.querySelector('#login-screen .login-card');
  card.querySelectorAll('.fg,.cf-turnstile').forEach(element=>element.style.display='none');
  document.getElementById('admin-2fa-step')?.remove();
  const panel=document.createElement('div');panel.id='admin-2fa-step';panel.className='fg';
  const intro=document.createElement('p');intro.className='login-sub';intro.style.marginBottom='14px';
  intro.textContent=S.twoFactorSetup?'Configure o Authenticator usando a chave abaixo e informe o primeiro código de 6 dígitos.':'Informe o código de 6 dígitos do Authenticator ou um código de recuperação.';
  panel.appendChild(intro);
  if(S.twoFactorSetup){
    const keyLabel=document.createElement('label');keyLabel.className='fl';keyLabel.textContent='Chave de configuração';panel.appendChild(keyLabel);
    const key=document.createElement('code');key.id='admin-2fa-setup-key';key.style.cssText='display:block;padding:12px;margin-bottom:10px;border-radius:8px;background:var(--bg);color:var(--accent);font-size:14px;letter-spacing:1px;word-break:break-all';key.textContent=data.setupKey;panel.appendChild(key);
    const actions=document.createElement('div');actions.style.cssText='display:flex;gap:8px;margin-bottom:14px';
    const copy=document.createElement('button');copy.type='button';copy.className='btn btn-outline';copy.textContent='Copiar chave';copy.addEventListener('click',async()=>{await navigator.clipboard.writeText((data.setupKey||'').replace(/\s/g,''));copy.textContent='Copiada';});actions.appendChild(copy);
    const open=document.createElement('a');open.className='btn btn-outline';open.textContent='Abrir Authenticator';open.href=data.provisioningUri;actions.appendChild(open);panel.appendChild(actions);
  }
  const codeLabel=document.createElement('label');codeLabel.className='fl';codeLabel.htmlFor='admin-2fa-code';codeLabel.textContent=S.twoFactorSetup?'Código de 6 dígitos':'Código do Authenticator ou recuperação';panel.appendChild(codeLabel);
  const code=document.createElement('input');code.id='admin-2fa-code';code.className='fi';code.type='text';code.autocomplete='one-time-code';code.inputMode=S.twoFactorSetup?'numeric':'text';code.maxLength=S.twoFactorSetup?6:32;panel.appendChild(code);
  document.getElementById('lbtn').before(panel);
  const button=document.getElementById('lbtn');button.textContent=S.twoFactorSetup?'Ativar e entrar':'Verificar e entrar';
  if(window.turnstile)window.turnstile.reset();
  code.focus();
}

async function doVerifyTwoFactor(){
  const code=document.getElementById('admin-2fa-code')?.value.trim()||'';
  const button=document.getElementById('lbtn');const err=document.getElementById('lerr');err.style.display='none';
  if(!code){err.textContent='Informe o código de autenticação.';err.style.display='block';return;}
  button.disabled=true;button.innerHTML='<div class="spinner"></div> Verificando...';
  try{
    const response=await fetch('/api/admin/login/verify',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({challengeId:S.loginChallengeId,code})});
    const data=await response.json().catch(()=>({}));
    if(!response.ok){err.textContent=data.erro||'Código inválido.';err.style.display='block';if(response.status===401&&String(data.erro||'').includes('Desafio'))resetAdminLoginFlow();return;}
    S.csrfToken=data.csrfToken;S.user=data.user;S.loginChallengeId=null;
    if(data.recoveryCodes?.length){showRecoveryCodes(data.recoveryCodes);return;}
    resetAdminLoginFlow();showApp();
    if(data.usedRecoveryCode)showAdminMessage('warning',`Código de recuperação utilizado. Restam ${data.remainingRecoveryCodes}.`);
  }catch(e){err.textContent='Erro de conexão. Tente novamente.';err.style.display='block';}
  finally{button.disabled=false;if(S.loginChallengeId)button.textContent=S.twoFactorSetup?'Ativar e entrar':'Verificar e entrar';}
}

function showRecoveryCodes(codes){
  document.getElementById('admin-2fa-step')?.remove();
  const panel=document.createElement('div');panel.id='admin-recovery-step';panel.className='fg';
  const title=document.createElement('p');title.className='login-title';title.style.fontSize='18px';title.textContent='Salve seus códigos de recuperação';panel.appendChild(title);
  const note=document.createElement('p');note.className='login-sub';note.textContent='Cada código funciona uma única vez. Guarde-os fora deste servidor; eles não serão exibidos novamente.';panel.appendChild(note);
  const list=document.createElement('pre');list.style.cssText='padding:12px;border-radius:8px;background:var(--bg);color:var(--txt);text-align:center;line-height:1.7;user-select:all';list.textContent=codes.join('\n');panel.appendChild(list);
  const copy=document.createElement('button');copy.type='button';copy.className='btn btn-outline btn-full';copy.textContent='Copiar códigos';copy.addEventListener('click',async()=>{await navigator.clipboard.writeText(codes.join('\n'));copy.textContent='Códigos copiados';});panel.appendChild(copy);
  document.getElementById('lbtn').before(panel);
  const button=document.getElementById('lbtn');button.textContent='Já salvei, entrar no painel';
}

function finishAdminLogin(){resetAdminLoginFlow();showApp();}

function resetAdminLoginFlow(){
  S.loginChallengeId=null;S.twoFactorSetup=false;
  document.getElementById('admin-2fa-step')?.remove();document.getElementById('admin-recovery-step')?.remove();
  const card=document.querySelector('#login-screen .login-card');card.querySelectorAll('.fg,.cf-turnstile').forEach(element=>element.style.display='');
  const button=document.getElementById('lbtn');button.textContent='Entrar no Painel';
  if(window.turnstile)window.turnstile.reset();
}
document.addEventListener('keydown',e=>{
  if(e.key==='Enter'&&document.getElementById('login-screen').style.display!=='none'){if(document.getElementById('admin-recovery-step'))finishAdminLogin();else if(S.loginChallengeId)doVerifyTwoFactor();else doLogin();}
  if(e.key==='Escape'&&document.querySelector('.modal-overlay.active')){if(adminConfirmResolver)resolveAdminConfirm(false);else closeModals();}
  if(e.key==='Tab'){
    const modal=document.querySelector('.modal-overlay.active');if(!modal)return;
    const focusable=[...modal.querySelectorAll('button:not([disabled]),input:not([disabled]):not([type="hidden"]),select:not([disabled]),textarea:not([disabled]),a[href]')];if(!focusable.length)return;
    const first=focusable[0],last=focusable[focusable.length-1];
    if(e.shiftKey&&document.activeElement===first){e.preventDefault();last.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus();}
  }
});
document.addEventListener('click',e=>{
  const trigger=e.target.closest('button');if(!trigger||document.querySelector('.modal-overlay.active'))return;
  adminLastModalTrigger=trigger;
  requestAnimationFrame(()=>document.querySelector('.modal-overlay.active input:not([type="hidden"]),.modal-overlay.active select,.modal-overlay.active textarea,.modal-overlay.active button')?.focus());
},true);
document.addEventListener('click',e=>{const p=document.getElementById('ad-link-picker');if(p&&!p.contains(e.target))closeAdLinkDropdown();});
function clearAdminSession(){localStorage.removeItem('premierAdmin');S.csrfToken=null;S.user=null;if(S.chart){S.chart.destroy();S.chart=null;}if(S.statusChart){S.statusChart.destroy();S.statusChart=null;}resetAdminLoginFlow();showLogin();}
async function doLogout(){if(hasUnsavedWhatsAppChanges()&&!await askAdminConfirm('Ha alteracoes nao salvas nesta mensagem. Deseja sair e descarta-las?'))return;try{await fetch(API+'/logout',{method:'POST',headers:hdrs()});}catch(e){}clearAdminSession();}
function hdrs(){const headers={'Content-Type':'application/json'};if(S.csrfToken)headers['X-CSRF-Token']=S.csrfToken;return headers;}
async function apiFetch(url){
  const r=await fetch(url,{headers:hdrs()});
  if(r.status===401){clearAdminSession();return null;}
  const data=await r.json().catch(()=>null);
  if(!r.ok){showAdminMessage('error', data?.erro||'Erro ao carregar dados.');return null;}
  return data;
}

const HDR={dashboard:['Dashboard','Cockpit financeiro e operacional'],financeiro:['Financeiro','Receita, planos e origem dos pedidos'],crm:['CRM','Clientes, renovacoes e fila comercial'],trials:['Testes grátis','Solicitações, liberações e uso do teste'],pedidos:['Pedidos','Gerenciar todos os pedidos'],usuarios:['Usuarios','Gerenciar usuarios cadastrados'],ad:['Active Directory','Gerenciar AD Local'],notificacoes:['Notificacoes','Mensagens automaticas do WhatsApp'],logs:['Logs','Acompanhar eventos e falhas da aplicacao']};
function setupCurrentView(){
  S.view=document.body?.dataset.view||S.view||'dashboard';
  document.querySelectorAll('.ni').forEach(e=>e.classList.remove('active'));
  document.getElementById('nav-'+S.view)?.classList.add('active');
  document.querySelectorAll('.view').forEach(e=>e.classList.add('active'));
  const[t,s]=HDR[S.view]||['',''];
  document.getElementById('htitle').textContent=t;
  document.getElementById('hsub').textContent=s;
}
function loadCurrentView(){
  if(S.view==='dashboard')loadDash();else if(S.view==='financeiro')loadFinanceiro();else if(S.view==='crm')loadCrm();else if(S.view==='trials')loadFreeTrials();else if(S.view==='pedidos')loadOrders();else if(S.view==='usuarios')loadUsers();else if(S.view==='ad')loadAd();else if(S.view==='notificacoes'){wireWhatsAppEditor();loadNotificacoes();}else if(S.view==='logs'){wireAdminLogs();loadAdminLogs();}
}
function viewFromAdminPath(pathname){return Object.keys(ADMIN_ROUTES).find(view=>ADMIN_ROUTES[view]===pathname)||null;}
function teardownCurrentView(nextView){
  clearInterval(adminLogsRefreshTimer);adminLogsRefreshTimer=null;
  if(S.view==='dashboard'&&nextView!=='dashboard'){
    if(S.chart){S.chart.destroy();S.chart=null;}
    if(S.statusChart){S.statusChart.destroy();S.statusChart=null;}
  }
  if(S.view==='notificacoes'&&nextView!=='notificacoes'){
    S.waEditorWired=false;S.waSelected=null;S.waOriginalBody='';S.waHistory=[];S.waHistoryIndex=-1;
  }
}
async function navigateAdmin(view,target=ADMIN_ROUTES[view],options={}){
  if(!target||view===S.view&&window.location.pathname===target)return;
  if(hasUnsavedWhatsAppChanges()&&!await askAdminConfirm('Ha alteracoes nao salvas nesta mensagem. Deseja sair e descarta-las?')){
    if(options.fromHistory)history.replaceState({adminView:S.view},'',ADMIN_ROUTES[S.view]);
    return;
  }
  adminNavigationController?.abort();
  const controller=new AbortController();adminNavigationController=controller;
  const request=++adminNavigationRequest;
  document.body.classList.add('admin-navigation-loading');document.body.setAttribute('aria-busy','true');
  try{
    const response=await fetch(target,{cache:'no-store',credentials:'same-origin',headers:{Accept:'text/html'},signal:controller.signal});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const nextDocument=new DOMParser().parseFromString(await response.text(),'text/html');
    const nextContent=nextDocument.querySelector('#main > .content');
    const nextView=nextDocument.body?.dataset.view;
    if(!nextContent||nextView!==view)throw new Error('Conteudo administrativo invalido');
    teardownCurrentView(view);
    document.querySelector('#main > .content').replaceWith(document.importNode(nextContent,true));
    document.body.dataset.view=view;S.view=view;
    document.title=nextDocument.title||document.title;
    if(options.push!==false)history.pushState({adminView:view},'',target);
    setupCurrentView();enhanceResponsiveTables(document.querySelector('#main > .content'));
    loadCurrentView();window.scrollTo({top:0,behavior:'instant'});
  }catch(error){
    if(error.name!=='AbortError')showAdminMessage('error','Nao foi possivel abrir esta area. Tente novamente.');
  }finally{
    if(request===adminNavigationRequest){document.body.classList.remove('admin-navigation-loading');document.body.removeAttribute('aria-busy');adminNavigationController=null;}
  }
}
function setupAdminNavigation(){
  if(document.body.dataset.adminNavigationWired==='true')return;
  document.body.dataset.adminNavigationWired='true';
  history.replaceState({adminView:S.view},'',window.location.href);
  document.addEventListener('click',event=>{
    const link=event.target.closest('a.ni[href]');
    if(!link||event.defaultPrevented||event.button!==0||event.metaKey||event.ctrlKey||event.shiftKey||event.altKey||link.target)return;
    const url=new URL(link.href,window.location.href);const view=viewFromAdminPath(url.pathname);
    if(url.origin!==window.location.origin||!view)return;
    event.preventDefault();navigateAdmin(view,url.pathname);
  });
  window.addEventListener('popstate',()=>{const view=viewFromAdminPath(window.location.pathname);if(view)navigateAdmin(view,window.location.pathname,{push:false,fromHistory:true});});
}
function go(v){
  const target=ADMIN_ROUTES[v];
  if(target){navigateAdmin(v,target);return;}
  S.view=v;setupCurrentView();loadCurrentView();
}
async function refresh(){if(hasUnsavedWhatsAppChanges()&&!await askAdminConfirm('Ha alteracoes nao salvas nesta mensagem. Deseja atualizar e descarta-las?'))return;S.dashData=null;loadCurrentView();}

function wireAdminLogs(){
  const controls=document.getElementById('logs-controls');
  if(!controls||controls.dataset.wired==='true')return;
  controls.dataset.wired='true';
  let searchTimer=null;
  document.getElementById('logs-level')?.addEventListener('change',loadAdminLogs);
  document.getElementById('logs-limit')?.addEventListener('change',loadAdminLogs);
  document.getElementById('logs-search')?.addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(loadAdminLogs,350);});
  document.getElementById('logs-users-only')?.addEventListener('change',()=>{
    const usersOnly=document.getElementById('logs-users-only')?.checked;
    const level=document.getElementById('logs-level');if(level)level.disabled=usersOnly;
    const search=document.getElementById('logs-search');if(search)search.placeholder=usersOnly?'Nome, e-mail, WhatsApp, IP ou navegador...':'Categoria ou mensagem...';
    loadAdminLogs();
  });
  document.getElementById('logs-auto')?.addEventListener('change',updateAdminLogsTimer);
  updateAdminLogsTimer();
}
function updateAdminLogsTimer(){
  clearInterval(adminLogsRefreshTimer);adminLogsRefreshTimer=null;
  if(document.getElementById('logs-auto')?.checked)adminLogsRefreshTimer=setInterval(loadAdminLogs,10000);
}
function logLevelBadge(level){
  const key=(level||'Information').toLowerCase();
  const cls=key==='critical'||key==='error'?'b-err':key==='warning'?'b-warn':key==='information'?'b-accent':'b-muted';
  return `<span class="badge ${cls}">${esc(level||'Information')}</span>`;
}
async function loadAdminLogs(){
  const body=document.getElementById('logs-body');if(!body)return;
  body.innerHTML='<tr><td colspan="5" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const qs=new URLSearchParams({
    level:document.getElementById('logs-level')?.value||'all',
    search:document.getElementById('logs-search')?.value||'',
    limit:document.getElementById('logs-limit')?.value||'300',
    usersOnly:document.getElementById('logs-users-only')?.checked?'true':'false'
  });
  const data=await apiFetch(`${API}/logs?${qs}`);
  if(!data){
    body.innerHTML='<tr><td colspan="5" class="empty">Não foi possível carregar os logs. Verifique o aviso exibido e tente novamente.</td></tr>';
    setText('logs-count','Falha ao carregar');
    return;
  }
  const entries=data.entries||[];
  setText('logs-count',`${entries.length} registro${entries.length===1?'':'s'} exibido${entries.length===1?'':'s'}`);
  setText('lupdate',`Atualizado ${new Date(data.generatedAt).toLocaleTimeString('pt-BR')}`);
  if(data.mode==='users'){
    setText('logs-head-time','Horário');setText('logs-head-level','Evento');setText('logs-head-category','Usuário');setText('logs-head-message','IP e localização');setText('logs-head-exception','Navegador e origem');
  }else{
    setText('logs-head-time','Horário');setText('logs-head-level','Nível');setText('logs-head-category','Categoria');setText('logs-head-message','Mensagem');setText('logs-head-exception','Exceção');
  }
  if(!entries.length){body.innerHTML='<tr><td colspan="5" class="empty">Nenhum log encontrado para estes filtros.</td></tr>';return;}
  if(data.mode==='users'){
    const eventLabel={cadastro:'Cadastro',login:'Login',logout:'Logout'};
    body.innerHTML=entries.map(entry=>`<tr>
      <td class="muted log-time">${fmtDateTime(entry.timestamp)}</td>
      <td><span class="badge ${entry.eventType==='cadastro'?'b-ok':entry.eventType==='login'?'b-accent':'b-muted'}">${eventLabel[entry.eventType]||esc(entry.eventType)}</span></td>
      <td><div class="ucell-name">${esc(entry.name)}</div><div class="ucell-email">${esc(entry.email)}</div><div class="ucell-email">${esc(entry.whatsapp||'WhatsApp não informado')}</div></td>
      <td><div class="log-message">IP: ${esc(entry.ipAddress||'não disponível')}</div><div class="ucell-email">País: ${esc(entry.countryCode||'não disponível')}</div></td>
      <td><div class="log-message">${esc(browserSummary(entry.userAgent))}</div><div class="ucell-email" title="${esc(entry.userAgent||'')}">User-Agent: ${esc(entry.userAgent||'não disponível')}</div><div class="ucell-email">Idioma: ${esc(entry.acceptLanguage||'não disponível')}</div><div class="ucell-email">Origem: ${esc(entry.referrerHost||'direta/não disponível')}</div></td>
    </tr>`).join('');
    return;
  }
  body.innerHTML=entries.map(entry=>`<tr>
    <td class="muted log-time">${fmtDateTime(entry.timestamp)}</td>
    <td>${logLevelBadge(entry.level)}</td>
    <td><span class="log-category" title="${esc(entry.category)}">${esc(entry.category)}</span></td>
    <td><span class="log-message">${esc(entry.message)}</span></td>
    <td>${entry.exception?`<span class="log-exception">${esc(entry.exception)}</span>`:'<span class="muted">&#8212;</span>'}</td>
  </tr>`).join('');
}

function dashUrl(){
  const sel=document.getElementById('dash-period');
  const period=sel?.value||S.dashPeriod||localStorage.getItem('premierAdminDashPeriod')||'month';
  S.dashPeriod=period;localStorage.setItem('premierAdminDashPeriod', period);
  const qs=new URLSearchParams({period});
  if(period==='custom'){
    const start=document.getElementById('dash-start')?.value;
    const end=document.getElementById('dash-end')?.value;
    if(start)qs.set('start',start);
    if(end)qs.set('end',end);
  }
  return API+'/dashboard?'+qs.toString();
}
function syncDashPeriodControls(){
  const periodSelect=document.getElementById('dash-period');
  if(periodSelect&&periodSelect.value!==S.dashPeriod)periodSelect.value=S.dashPeriod;
  const custom=periodSelect?.value==='custom';
  document.querySelectorAll('.dash-custom').forEach(e=>e.classList.toggle('hidden',!custom));
}
function handlePeriodChange(){
  const periodSelect=document.getElementById('dash-period');if(!periodSelect)return;
  S.dashPeriod=periodSelect.value;
  const custom=periodSelect.value==='custom';
  document.querySelectorAll('.dash-custom').forEach(e=>e.classList.toggle('hidden',!custom));
}
function reloadDashViews(){S.dashData=null;loadCurrentView();}
async function ensureDashData(){
  if(S.dashData)return S.dashData;
  const data=await apiFetch(dashUrl());
  if(!data)return null;
  S.dashData=data;
  return data;
}
async function loadDash(){
  syncDashPeriodControls();
  const [data,chartReady]=await Promise.all([ensureDashData(),ensureChartJs()]);if(!data)return;
  const st=data.stats;
  document.getElementById('period-label').textContent=(data.period?.label||'Periodo')+' | '+fmtDate(data.period?.start)+' a '+fmtDate(data.period?.end);
  document.getElementById('s-rev').textContent=fmtCur(st.revenue);
  setDelta('s-rev-chg',st.revenue,st.priorRevenue,'vs periodo anterior');
  document.getElementById('s-ticket').textContent=fmtCur(st.averageTicket);
  document.getElementById('s-conv').textContent=fmtPct(st.conversionRate)+' de conversao';
  document.getElementById('s-active').textContent=st.activeLicenses;
  document.getElementById('s-active-sub').textContent=st.expiringSoon+' vencendo em 7 dias';
  document.getElementById('s-orders').textContent=st.ordersInPeriod;
  document.getElementById('s-orders-sub').textContent=st.paidOrders+' pagos | '+st.pendingOrders+' pendentes';
  document.getElementById('s-customers').textContent=st.activeCustomers;
  const nu=document.getElementById('s-users-new');nu.textContent='+'+st.newUsers+' novos usuarios';nu.className='stat-note '+(st.newUsers>0?'up':'neutral');
  document.getElementById('s-mrr').textContent=fmtCur(st.estimatedMrr);
  document.getElementById('s-total').textContent='Total historico '+fmtCur(st.totalRevenue);
  if(chartReady){renderChart(data.revenueSeries||data.monthlyRevenue||[]);renderStatusChart(data.statusBreakdown||[]);}
  renderAnalyticsFunnel(data.analyticsFunnel||[]);
  renderActionList('action-list',data.actionQueue||[]);
  renderTopMini(data.topCustomers||[]);
  renderRecent(data.recentOrders||[]);
  renderFinanceiro(data);
  renderCrm(data);
  document.getElementById('lupdate').textContent='Atualizado: '+new Date().toLocaleTimeString('pt-BR');
}
function renderRecent(rows){
  const rb=document.getElementById('recent-body');
  if(!rows.length){rb.innerHTML='<tr><td colspan="7" class="empty">Nenhum pedido ainda.</td></tr>';return;}
  rb.innerHTML=rows.map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${esc(o.period)}</td><td>${o.computers}PC/${o.wydsPerComputer}sl</td><td class="csp-d021">${fmtCur(o.totalPrice)}</td><td>${sbadge(o.status,o.isActive)}</td><td>${o.isActive?'<span class="csp-d022">'+fmtDate(o.expiresAt)+'</span>':'<span class="muted">'+fmtDate(o.expiresAt)+'</span>'}</td><td class="muted">${fmtDate(o.createdAt)}</td></tr>`).join('');
}
function renderAnalyticsFunnel(rows){
  const el=document.getElementById('analytics-funnel');if(!el)return;
  const order=['landing_viewed','simulator_viewed','auth_opened','signup_completed','checkout_attempted','pix_created','payment_received','access_delivered'];
  const labels={landing_viewed:'Landing',simulator_viewed:'Simulador',auth_opened:'Autenticação',signup_completed:'Cadastro concluído',checkout_attempted:'Tentativa de compra',pix_created:'Pix gerado',payment_received:'Pagamento recebido',access_delivered:'Acesso entregue'};
  const map=new Map(rows.map(x=>[x.eventName,x]));
  el.innerHTML=order.map(name=>{const item=map.get(name)||{events:0,sessions:0};return `<div class="funnel-step"><div class="funnel-label">${labels[name]}</div><div class="funnel-value">${item.sessions}</div><div class="funnel-note">${item.events} eventos</div></div>`;}).join('');
}
function renderChart(mr){
  const el=document.getElementById('revenue-chart');if(!el)return;
  const ctx=el.getContext('2d');
  if(S.chart)S.chart.destroy();
  S.chart=new Chart(ctx,{type:'line',data:{labels:mr.map(m=>m.label||m.month),datasets:[{label:'Receita (R$)',data:mr.map(m=>parseFloat(m.revenue||0)),borderColor:'#3b82f6',backgroundColor:'rgba(59,130,246,.07)',borderWidth:2,fill:true,tension:.35,pointBackgroundColor:'#3b82f6',pointRadius:4,pointHoverRadius:7},{label:'Pedidos',data:mr.map(m=>parseFloat(m.orders||0)),borderColor:'#3fb950',backgroundColor:'rgba(63,185,80,.06)',borderWidth:2,tension:.35,yAxisID:'y1'}]},options:{responsive:true,plugins:{legend:{labels:{color:'#7d8590',boxWidth:10,font:{size:11}}},tooltip:{backgroundColor:'#1c2128',borderColor:'#30363d',borderWidth:1,titleColor:'#e6edf3',bodyColor:'#7d8590',callbacks:{label:c=>c.dataset.label==='Pedidos'?c.parsed.y+' pedidos':'R$ '+c.parsed.y.toFixed(2).replace('.',',')}}},scales:{x:{grid:{color:'rgba(48,54,61,.4)'},ticks:{color:'#7d8590',font:{size:11}}},y:{grid:{color:'rgba(48,54,61,.4)'},ticks:{color:'#7d8590',font:{size:11},callback:v=>'R$ '+v.toFixed(0)}},y1:{position:'right',grid:{drawOnChartArea:false},ticks:{color:'#7d8590',font:{size:11},precision:0}}}}});
}
function renderStatusChart(rows){
  const el=document.getElementById('status-chart');if(!el)return;
  const ctx=el.getContext('2d');
  if(S.statusChart)S.statusChart.destroy();
  S.statusChart=new Chart(ctx,{type:'doughnut',data:{labels:rows.map(x=>statusLabel(x.status)),datasets:[{data:rows.map(x=>x.count),backgroundColor:['#3fb950','#e3b341','#3b82f6','#f85149','#7d8590'],borderColor:'#1c2128',borderWidth:2}]},options:{responsive:true,plugins:{legend:{position:'bottom',labels:{color:'#7d8590',boxWidth:10,font:{size:11}}}}}});
}

async function loadFinanceiro(){const data=await ensureDashData();if(data)renderFinanceiro(data);}
async function loadCrm(){const data=await ensureDashData();if(data)renderCrm(data);}
function renderFinanceiro(data){
  const st=data.stats||{};
  setText('fin-revenue',fmtCur(st.revenue));
  setDelta('fin-rev-note',st.revenue,st.priorRevenue,'vs periodo anterior');
  setText('fin-total',fmtCur(st.totalRevenue));
  setText('fin-manual',st.manualPaidOrders||0);
  setText('fin-conv',fmtPct(st.conversionRate));
  renderBreakdownList('plan-list',data.planBreakdown||[],x=>esc(x.period),x=>`${x.count} pedidos | ${x.computers} PCs | ${x.slots} slots`,x=>fmtCur(x.revenue));
  renderBreakdownList('type-list',data.orderTypeBreakdown||[],x=>esc(x.type),x=>`${x.count} pedidos`,x=>fmtCur(x.revenue));
  const body=document.getElementById('status-body');
  if(body)body.innerHTML=(data.statusBreakdown||[]).length?(data.statusBreakdown||[]).map(x=>`<tr><td>${sbadge(x.status,x.status==='pago')}</td><td>${x.count}</td><td class="csp-d021">${fmtCur(x.revenue)}</td></tr>`).join(''):'<tr><td colspan="3" class="empty">Sem pedidos no periodo.</td></tr>';
}
function renderCrm(data){
  const st=data.stats||{};
  setText('crm-active',st.activeCustomers||0);
  setText('crm-expiring',st.expiringSoon||0);
  setText('crm-delivery',st.pendingDeliveryOrders||0);
  setText('crm-new',st.newUsers||0);
  renderActionList('crm-actions',data.actionQueue||[]);
  const upcoming=document.getElementById('upcoming-body');
  if(upcoming)upcoming.innerHTML=(data.upcomingExpirations||[]).length?(data.upcomingExpirations||[]).map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${esc(o.period)} <span class="muted">${o.computers}PC/${o.wydsPerComputer}sl</span></td><td class="csp-d021">${fmtCur(o.totalPrice)}</td><td>${daysUntil(o.expiresAt)}</td><td class="muted">${esc(o.whatsapp||'-')}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">Nenhuma licenca ativa encontrada.</td></tr>';
  const top=document.getElementById('top-customers-body');
  if(top)top.innerHTML=(data.topCustomers||[]).length?(data.topCustomers||[]).map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${o.orders}</td><td class="csp-d023">${fmtCur(o.revenue)}</td><td class="muted">${fmtDate(o.lastOrderAt)}</td><td class="muted">${esc(o.whatsapp||'-')}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">Sem clientes pagantes neste periodo.</td></tr>';
}
function renderBreakdownList(id,rows,title,sub,value){
  const el=document.getElementById(id);if(!el)return;
  if(!rows.length){el.innerHTML='<div class="empty">Sem dados no periodo.</div>';return;}
  const max=Math.max(...rows.map(x=>parseFloat(x.revenue||x.count||0)),1);
  el.innerHTML=rows.map(x=>{const v=parseFloat(x.revenue||x.count||0);return `<div class="insight-item"><div class="insight-main"><div class="insight-title">${title(x)}</div><div class="insight-sub">${sub(x)}</div><div class="progress"><span data-progress="${Math.max(6,Math.round(v/max*100))}"></span></div></div><div class="insight-val">${value(x)}</div></div>`;}).join('');
  el.querySelectorAll('[data-progress]').forEach(bar=>{bar.style.width=`${bar.dataset.progress}%`;});
}
function renderActionList(id,rows){
  const el=document.getElementById(id);if(!el)return;
  if(!rows.length){el.innerHTML='<div class="empty">Nenhuma acao urgente agora.</div>';return;}
  el.innerHTML=rows.map(x=>`<div class="insight-item"><div class="insight-main"><div class="kpi-note"><span class="action-chip">${esc(x.type)}</span><span class="muted">${fmtDate(x.eventAt)}</span></div><div class="insight-title csp-d024">${esc(x.userName)}</div><div class="insight-sub">${esc(x.email)}${x.whatsapp?' | '+esc(x.whatsapp):''}</div></div><div class="insight-val">${fmtCur(x.totalPrice)}</div></div>`).join('');
}
function renderTopMini(rows){
  const el=document.getElementById('top-customers-mini');if(!el)return;
  if(!rows.length){el.innerHTML='<div class="empty">Sem clientes pagantes neste periodo.</div>';return;}
  el.innerHTML=rows.slice(0,5).map(x=>`<div class="insight-item"><div class="insight-main"><div class="insight-title">${esc(x.userName)}</div><div class="insight-sub">${x.orders} pedidos | ${esc(x.email)}</div></div><div class="insight-val">${fmtCur(x.revenue)}</div></div>`).join('');
}
function setDelta(id,current,previous,label){
  const el=document.getElementById(id);if(!el)return;
  current=parseFloat(current||0);previous=parseFloat(previous||0);
  if(previous>0){const d=((current-previous)/previous*100);el.textContent=(d>=0?'+':'-')+Math.abs(d).toFixed(1)+'% '+label;el.className='stat-note '+(d>=0?'up':'down');}
  else{el.textContent=current>0?'Sem base anterior':'Primeiro periodo';el.className='stat-note neutral';}
}
function setText(id,value){const el=document.getElementById(id);if(el)el.textContent=value;}
function fmtPct(v){return (parseFloat(v||0)).toFixed(1).replace('.',',')+'%';}
function initial(s){return esc((s||'?').trim()[0]||'?').toUpperCase();}
function statusLabel(s){return({pago:'Pago',pendente:'Pendente',cancelado:'Cancelado',expirado:'Expirado PIX'}[s]||s||'Indefinido');}
function daysUntil(d){const dt=new Date(d);if(isNaN(dt))return'--';const days=Math.ceil((dt-new Date())/86400000);if(days<0)return'vencida';if(days===0)return'hoje';if(days===1)return'amanha';return days+' dias';}
function renderPaymentId(value,paidManually,createdManually){
  const manual=paidManually?'<span class="payment-manual">Pago manual</span>':createdManually?'<span class="payment-manual">Pedido manual</span>':'';
  if(!value)return'<span class="muted">&#8212;</span>'+manual;
  const safe=esc(value);
  return`<details class="payment-id-details"><summary title="Exibir ID do Asaas">ID...</summary><code title="${safe}">${safe}</code></details>${manual}`;
}
async function loadOrders(p){
  if(p)S.ordPage=p;
  updateSortableHeaderState();
  const body=document.getElementById('orders-body');body.innerHTML='<tr><td colspan="13" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const qs=new URLSearchParams({status:S.ordFilter,page:S.ordPage,limit:20,sortBy:S.ordSort,sortDir:S.ordSortDir});
  const data=await apiFetch(`${API}/orders?${qs}`);if(!data)return;
  if(!data.orders?.length){body.innerHTML='<tr><td colspan="13" class="empty">Nenhum pedido encontrado.</td></tr>';}
  else{body.innerHTML=data.orders.map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div class="cell-shrink"><div class="ucell-name cell-ellipsis" title="${esc(o.userName)}">${esc(o.userName)}</div><div class="ucell-email cell-ellipsis" title="${esc(o.email)}">${esc(o.email)}</div></div></div></td><td class="muted"><span class="cell-ellipsis" title="${esc(o.whatsapp||'Não informado')}">${esc(o.whatsapp||'&#8212;')}</span></td><td><span class="cell-ellipsis" title="${esc(o.wydServerName||'Não informado')}">${esc(o.wydServerName||'&#8212;')}</span></td><td>${esc(o.period)} <span class="muted">(${o.days}d)</span></td><td>${o.computers}PC/${o.wydsPerComputer}sl</td><td class="csp-d021">${fmtCur(o.totalPrice)}</td><td class="payment-id-cell">${renderPaymentId(o.asaasPaymentId,o.paidManually,o.createdManually)}</td><td>${sbadge(o.status,o.isActive)}</td><td><label class="inline-check compact-check" title="${o.delivered?'Marcar como não entregue':'Marcar como entregue'}"><input type="checkbox" aria-label="Pedido entregue" data-admin-change="toggle-order-delivery" data-order-id="${esc(o.id)}" ${o.status!=='pago'?'disabled':''} ${o.delivered?'checked':''}></label></td><td>${o.isActive?'<span class="license-state active"><strong>Ativa</strong><small>até '+fmtDate(o.expiresAt)+'</small></span>':'<span class="license-state muted"><strong>Expirada</strong><small>'+fmtDate(o.expiresAt)+'</small></span>'}</td><td class="muted">${fmtDate(o.createdAt)}</td><td>${o.canceledAt?'<span class="muted csp-d025">'+(o.canceledWasPaid&&!o.paidManually&&!(o.asaasPaymentId||'').startsWith('MANUAL_')?(o.refunded?'C/R ':'S/R '):'')+fmtDate(o.canceledAt)+'</span>':'<span class="muted">&#8212;</span>'}</td><td><div class="action-row">${(o.status==='pendente'||o.status==='expirado')?`<button class="btn btn-outline csp-d026" title="Marcar como pago manualmente" data-admin-click="mark-order-paid" data-order-id="${esc(o.id)}">Pago manual</button>`:''}${o.createdManually&&o.status==='pendente'&&!o.asaasPaymentId?`<button class="btn btn-outline csp-d027" data-admin-click="delete-order" data-order-id="${esc(o.id)}" title="Excluir pedido manual">&#128465;</button>`:(o.status==='pago'||o.status==='pendente')?`<button class="btn btn-outline csp-d028" data-admin-click="open-cancel-order" data-order-id="${esc(o.id)}" data-paid="${o.status==='pago' && !o.paidManually && !(o.asaasPaymentId||'').startsWith('MANUAL_')}">Cancelar</button>`:''}${o.status==='cancelado'?`<button class="btn btn-outline csp-d027" data-admin-click="delete-order" data-order-id="${esc(o.id)}" title="Excluir">&#128465;</button>`:''}</div></td></tr>`).join('');}
  renderPag('orders',data.total,S.ordPage,20);
}
function setFilter(f,btn){S.ordFilter=f;S.ordPage=1;document.querySelectorAll('#fbar .fb').forEach(b=>b.classList.remove('active'));btn.classList.add('active');loadOrders();}

function browserSummary(userAgent){
  const ua=userAgent||'';
  const patterns=[[/Edg\/([\d.]+)/,'Edge'],[/OPR\/([\d.]+)/,'Opera'],[/Chrome\/([\d.]+)/,'Chrome'],[/Firefox\/([\d.]+)/,'Firefox'],[/Version\/([\d.]+).*Safari/,'Safari']];
  const browser=patterns.map(([re,name])=>{const m=ua.match(re);return m?`${name} ${m[1]}`:null;}).find(Boolean)||'Não identificado';
  const os=/Windows NT 10/.test(ua)?'Windows':/Android/.test(ua)?'Android':/iPhone|iPad/.test(ua)?'iOS/iPadOS':/Mac OS X/.test(ua)?'macOS':/Linux/.test(ua)?'Linux':'SO não identificado';
  return `${browser} · ${os}`;
}
let registrationInfoTooltip=null;
let registrationInfoAnchor=null;
function hideRegistrationInfo(){
  registrationInfoTooltip?.remove();
  registrationInfoAnchor?.removeAttribute('aria-describedby');
  registrationInfoTooltip=null;
  registrationInfoAnchor=null;
}
function showRegistrationInfo(button){
  const text=button?.dataset.tooltip;
  if(!text)return;
  hideRegistrationInfo();
  const tooltip=document.createElement('div');
  tooltip.id='registration-info-tooltip';
  tooltip.className='registration-info-tooltip';
  tooltip.setAttribute('role','tooltip');
  tooltip.textContent=text;
  document.body.appendChild(tooltip);
  const buttonRect=button.getBoundingClientRect();
  const tooltipRect=tooltip.getBoundingClientRect();
  const left=Math.min(window.innerWidth-tooltipRect.width-12,Math.max(12,buttonRect.right-tooltipRect.width));
  const preferredTop=buttonRect.top-tooltipRect.height-9;
  const top=preferredTop>=12?preferredTop:buttonRect.bottom+9;
  tooltip.style.left=`${left}px`;
  tooltip.style.top=`${top}px`;
  button.setAttribute('aria-describedby',tooltip.id);
  registrationInfoTooltip=tooltip;
  registrationInfoAnchor=button;
}
window.addEventListener('resize',hideRegistrationInfo);
window.addEventListener('scroll',hideRegistrationInfo,true);
function registrationInfo(u){
  const lines=[];
  if(u.registrationIp)lines.push(`IP: ${u.registrationIp}`);
  if(u.registrationUserAgent){lines.push(`Navegador: ${browserSummary(u.registrationUserAgent)}`);lines.push(`User-Agent: ${u.registrationUserAgent}`);}
  if(u.registrationAcceptLanguage)lines.push(`Idioma: ${u.registrationAcceptLanguage}`);
  if(u.registrationCountryCode)lines.push(`País (Cloudflare): ${u.registrationCountryCode}`);
  if(u.registrationReferrerHost)lines.push(`Origem: ${u.registrationReferrerHost}`);
  if(u.registrationSource){const sourceLabel={admin:'Criado no Admin',site:'Cadastro no site',login_recovery:'Recuperado no login'}[u.registrationSource]||u.registrationSource;lines.push(`Canal: ${sourceLabel}`);}
  if(!lines.length)lines.push('Metadados indisponíveis: cadastro anterior à coleta.');
  const text=lines.join('\n');
  return `<button type="button" class="registration-info" data-tooltip="${esc(text)}" aria-label="Informações técnicas do cadastro: ${esc(text)}" data-admin-mouseenter="show-registration-info" data-admin-mouseleave="hide-registration-info" data-admin-focus="show-registration-info" data-admin-blur="hide-registration-info">i</button>`;
}

async function loadUsers(p){
  if(p)S.usrPage=p;
  updateSortableHeaderState();
  const body=document.getElementById('users-body');body.innerHTML='<tr><td colspan="10" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const qs=new URLSearchParams({page:S.usrPage,limit:20,search:S.usrSearch,sortBy:S.usrSort,sortDir:S.usrSortDir});
  const data=await apiFetch(`${API}/users?${qs}`);if(!data)return;
  if(!data.users?.length){body.innerHTML='<tr><td colspan="10" class="empty">Nenhum usuario encontrado.</td></tr>';}
  else {
      _allLocalUsers = data.users;
      body.innerHTML=data.users.map(u=>`<tr>
          <td><div class="ucell"><div class="avatar avatar-lg">${initial(u.name)}</div><div><div class="ucell-name">${esc(u.name)}</div><div class="ucell-email">${esc(u.email)}</div></div></div></td>
          <td class="muted">${esc(u.whatsapp||'-')}</td>
          <td>${u.isActive?'<span class="badge b-ok">Ativa</span>':'<span class="badge b-err">Inativa</span>'}</td>
          <td><button class="btn btn-outline csp-d027" data-admin-click="open-ad-link" data-user-id="${esc(u.id)}">${u.adUsername?esc(u.adUsername):'Vincular'}</button></td>
          <td class="csp-d029">${u.totalOrders}</td>
          <td class="csp-d023">${fmtCur(u.totalSpent)}</td>
          <td class="csp-d030">${u.activeLicenses>0?'<span class="badge b-ok">'+u.activeLicenses+' ativa'+(u.activeLicenses>1?'s':'')+'</span>':'<span class="muted">&#8212;</span>'}</td>
          <td>${u.emailConfirmed?'<label class="inline-check"><input type="checkbox" checked disabled><span>Confirmado</span></label>':`<div class="email-confirmation-actions"><input type="checkbox" data-user-id="${esc(u.id)}" data-admin-change="confirm-email" aria-label="Confirmar e-mail manualmente" title="Confirmar e-mail manualmente"><button class="btn btn-outline" data-admin-click="resend-email" data-user-id="${esc(u.id)}">Reenviar</button></div>`}</td>
          <td class="muted"><span class="registration-date">${fmtDate(u.createdAt)} ${registrationInfo(u)}</span></td>
          <td>
              <details class="action-details"><summary class="btn btn-outline">Mais ações</summary><div class="action-menu-panel">
                  <button class="btn btn-outline csp-d031" title="Editar" data-admin-click="open-local-user" data-user-id="${esc(u.id)}">Editar</button>
                  <button class="btn btn-outline local-user-toggle ${u.isActive?'is-deactivate':'is-activate'}" data-admin-click="toggle-local-user" data-user-id="${esc(u.id)}" data-activate="${!u.isActive}" title="${u.id === S.user?.id ? 'Voc&ecirc; n&atilde;o pode inativar sua pr&oacute;pria conta' : (u.isActive?'Inativar cadastro':'Ativar cadastro')}" ${u.id === S.user?.id && u.isActive ? 'disabled' : ''}>${u.isActive?'Inativar':'Ativar'}</button>
                  <button class="btn btn-outline csp-d032" title="Excluir" data-admin-click="delete-local-user" data-user-id="${esc(u.id)}" data-has-ad="${!!u.adUsername}">Excluir</button>
              </div></details>
          </td>
      </tr>`).join('');
  }
  renderPag('users',data.total,S.usrPage,20);
}
let st_=null;function handleSearch(v){S.usrSearch=v;S.usrPage=1;clearTimeout(st_);st_=setTimeout(()=>loadUsers(),400);}

function trialBadge(status){
  const map={nao_solicitado:['Nunca solicitou','b-muted'],solicitado:['Solicitado','b-warn'],liberado:['Liberado','b-ok'],utilizado:['Utilizado','b-accent'],recusado:['Recusado','b-err'],cancelado:['Cancelado','b-muted']};
  const item=map[status]||map.nao_solicitado;return `<span class="badge ${item[1]}">${item[0]}</span>`;
}
function trialActions(u){
  const buttons=[];
  if(!u.requestId&&!u.hasPaidOrder){
    buttons.push(`<button class="btn btn-outline trial-release" data-admin-click="release-free-trial" data-user-id="${esc(u.userId)}">Liberar teste</button>`);
  }
  if(u.status==='solicitado'){
    if(!u.hasPaidOrder)buttons.push(`<button class="btn btn-outline trial-release" data-admin-click="update-free-trial" data-request-id="${esc(u.requestId)}" data-update-action="release" data-confirm="Liberar este teste grátis?">Liberar</button>`);
    buttons.push(`<button class="btn btn-outline danger-action" data-admin-click="update-free-trial" data-request-id="${esc(u.requestId)}" data-update-action="reject" data-confirm="Recusar esta solicitação?">Recusar</button>`);
  }
  if(u.status==='liberado'){
    buttons.push(`<button class="btn btn-outline trial-used" data-admin-click="update-free-trial" data-request-id="${esc(u.requestId)}" data-update-action="mark-used" data-confirm="Confirmar que o teste foi realmente utilizado?">Marcar utilizado</button>`);
    buttons.push(`<button class="btn btn-outline danger-action" data-admin-click="update-free-trial" data-request-id="${esc(u.requestId)}" data-update-action="cancel" data-confirm="Cancelar esta liberação sem marcar uso?">Cancelar</button>`);
  }
  if(u.whatsapp){const phone=u.whatsapp.replace(/\D/g,'');buttons.push(`<a class="btn btn-outline" target="_blank" rel="noopener" href="https://wa.me/55${phone.replace(/^55/,'')}">WhatsApp</a>`);}
  if(u.status==='recusado'||u.status==='utilizado'){
    const description=u.status==='utilizado'?'teste utilizado':'solicitação recusada';
    buttons.push(`<button class="btn btn-outline danger-action csp-d033" data-admin-click="delete-free-trial" data-request-id="${esc(u.requestId)}" data-status="${esc(u.status)}" title="Excluir ${description}" aria-label="Excluir ${description}">&#128465;</button>`);
  }
  return buttons.length?`<div class="action-row">${buttons.join('')}</div>`:'<span class="muted">Sem ação</span>';
}
async function loadFreeTrials(p){
  if(p)S.trialPage=p;
  updateSortableHeaderState();
  const body=document.getElementById('trials-body');if(!body)return;
  body.innerHTML='<tr><td colspan="9" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const qs=new URLSearchParams({filter:S.trialFilter,page:S.trialPage,limit:20,search:S.trialSearch,sortBy:S.trialSort,sortDir:S.trialSortDir});
  const data=await apiFetch(`${API}/free-trials?${qs}`);if(!data)return;
  setText('trial-never',data.stats?.neverRequested??0);setText('trial-not-used',data.stats?.notUsed??0);setText('trial-requested',data.stats?.requested??0);setText('trial-released',data.stats?.released??0);setText('trial-used',data.stats?.used??0);
  if(!data.users?.length)body.innerHTML='<tr><td colspan="9" class="empty">Nenhum usuário encontrado para este filtro.</td></tr>';
  else body.innerHTML=data.users.map(u=>`<tr><td><div class="ucell"><div class="avatar">${initial(u.name)}</div><div><div class="ucell-name">${esc(u.name)}</div><div class="ucell-email">${esc(u.email)}</div></div></div></td><td class="muted">${esc(u.whatsapp||'-')}</td><td>${trialBadge(u.status)}${u.hasPaidOrder?'<small class="trial-count">Cliente com pedido pago</small>':''}</td><td class="muted"><span class="registration-date">${fmtDate(u.createdAt)} ${registrationInfo(u)}</span></td><td class="muted">${u.firstRequestedAt?fmtDateTime(u.firstRequestedAt):'—'}</td><td class="muted">${u.lastRequestedAt?fmtDateTime(u.lastRequestedAt):'—'}${u.requestCount>1?`<small class="trial-count">${u.requestCount} solicitações</small>`:''}</td><td class="muted">${u.releasedAt?fmtDateTime(u.releasedAt):'—'}</td><td class="muted">${u.usedAt?fmtDateTime(u.usedAt):'—'}</td><td>${trialActions(u)}</td></tr>`).join('');
  enhanceResponsiveTables();
  renderTrialPagination(data.total,data.page,data.limit);
  setText('lupdate','Atualizado: '+new Date().toLocaleTimeString('pt-BR'));
}
function setTrialFilter(filter,button){S.trialFilter=filter;S.trialPage=1;document.querySelectorAll('#trial-filters .fb').forEach(b=>b.classList.remove('active'));button.classList.add('active');loadFreeTrials();}
let trialSearchTimer=null;function handleTrialSearch(value){S.trialSearch=value;S.trialPage=1;clearTimeout(trialSearchTimer);trialSearchTimer=setTimeout(()=>loadFreeTrials(),350);}
function renderTrialPagination(total,current,limit){
  const count=document.getElementById('trials-count'),buttons=document.getElementById('trials-pag');const pages=Math.ceil(total/limit);
  count.textContent=total?`${(current-1)*limit+1}-${Math.min(current*limit,total)} de ${total}`:'0 resultados';
  if(pages<=1){buttons.innerHTML='';return;}
  let html=`<button class="pb" data-admin-click="load-page" data-page-type="trials" data-page="${current-1}" ${current===1?'disabled':''}>&lsaquo;</button>`;
  for(let i=1;i<=pages;i++){if(i===1||i===pages||(i>=current-1&&i<=current+1))html+=`<button class="pb ${i===current?'active':''}" data-admin-click="load-page" data-page-type="trials" data-page="${i}">${i}</button>`;else if(i===current-2||i===current+2)html+='<span class="muted">…</span>';}
  html+=`<button class="pb" data-admin-click="load-page" data-page-type="trials" data-page="${current+1}" ${current===pages?'disabled':''}>&rsaquo;</button>`;buttons.innerHTML=html;
}
async function updateFreeTrial(id,action,message){
  if(!await askAdminConfirm(message,{title:'Teste grátis',confirmText:'Confirmar'}))return;
  const response=await fetch(`${API}/free-trials/${encodeURIComponent(id)}/${encodeURIComponent(action)}`,{method:'PUT',headers:hdrs()});
  const data=await response.json().catch(()=>null);
  if(!response.ok){showAdminMessage('error',data?.erro||'Não foi possível atualizar o teste.');return;}
  showAdminMessage('success',data?.msg||'Situação atualizada.');loadFreeTrials();
}
async function releaseFreeTrialManually(userId){
  if(!await askAdminConfirm('Liberar manualmente um teste grátis para este usuário?',{title:'Teste grátis',confirmText:'Liberar'}))return;
  const response=await fetch(`${API}/free-trials/users/${encodeURIComponent(userId)}/release`,{method:'POST',headers:hdrs()});
  const data=await response.json().catch(()=>null);
  if(!response.ok){showAdminMessage('error',data?.erro||'Não foi possível liberar o teste.');return;}
  showAdminMessage('success',data?.msg||'Teste grátis liberado manualmente.');loadFreeTrials();
}
async function deleteFreeTrial(id,status){
  const description=status==='utilizado'?'este teste utilizado':'esta solicitação recusada';
  if(!await askAdminConfirm(`Excluir permanentemente ${description}? O usuário voltará a aparecer como nunca solicitou.`,{title:'Excluir solicitação',confirmText:'Excluir'}))return;
  const response=await fetch(`${API}/free-trials/${encodeURIComponent(id)}`,{method:'DELETE',headers:hdrs()});
  const data=await response.json().catch(()=>null);
  if(!response.ok){showAdminMessage('error',data?.erro||'Não foi possível excluir a solicitação.');return;}
  showAdminMessage('success',data?.msg||'Solicitação excluída.');loadFreeTrials();
}

function renderPag(type,total,cur,limit){
  const pages=Math.ceil(total/limit);const cnt=document.getElementById(type+'-count');const btns=document.getElementById(type+'-pag');
  if(total>0){const s=(cur-1)*limit+1;const e=Math.min(cur*limit,total);cnt.textContent=s+'-'+e+' de '+total;}else{cnt.textContent='0 resultados';}
  if(pages<=1){btns.innerHTML='';return;}
  let h=`<button class="pb" data-admin-click="load-page" data-page-type="${type}" data-page="${cur-1}" ${cur===1?'disabled':''}>&lsaquo;</button>`;
  for(let i=1;i<=pages;i++){if(i===1||i===pages||(i>=cur-1&&i<=cur+1))h+=`<button class="pb ${i===cur?'active':''}" data-admin-click="load-page" data-page-type="${type}" data-page="${i}">${i}</button>`;else if(i===cur-2||i===cur+2)h+=`<span class="csp-d034">&hellip;</span>`;}
  h+=`<button class="pb" data-admin-click="load-page" data-page-type="${type}" data-page="${cur+1}" ${cur===pages?'disabled':''}>&rsaquo;</button>`;
  btns.innerHTML=h;
}

const WA_SAMPLE={cliente_nome:'Joao Silva',cliente_whatsapp:'5534999187189',cliente_email:'cliente@exemplo.com',plano:'mensal',dias:'30',valor:'149,90',computadores:'1',slots:'4',pedido_id:'pay_123456789',ambiente:'PRODUCAO',data_pagamento:'11/07/2026 14:30'};
const WA_EMOJIS=['✅','💰','⚠️','🚀','🔔','📦','💳','🛠️','📌','⏳','🎉','🙏','📲','🧾','🔐','⭐','🔥','👉','👇','☑️','❌','💬','📅','⏰'];
function getWaBody(){return document.getElementById('wa-body');}
async function loadNotificacoes(){const list=document.getElementById('wa-template-list');if(list)list.innerHTML='<div class="loading"><div class="spinner"></div> Carregando...</div>';const data=await apiFetch(API+'/whatsapp/templates');if(!data)return;S.waTemplates=data.templates||[];renderWhatsAppTemplateList();selectWhatsAppTemplate(S.waSelected||S.waTemplates[0]?.key,true);document.getElementById('lupdate').textContent='Atualizado: '+new Date().toLocaleTimeString('pt-BR');}
function renderWhatsAppTemplateList(){const list=document.getElementById('wa-template-list');if(!list)return;if(!S.waTemplates.length){list.innerHTML='<div class="empty">Nenhuma mensagem cadastrada.</div>';return;}list.innerHTML=S.waTemplates.map(t=>`<button class="template-option ${S.waSelected===t.key?'active':''}" data-admin-click="select-whatsapp-template" data-template-key="${esc(t.key)}"><span class="template-option-title">${esc(t.title)}</span><span class="template-option-sub">${esc(t.audience)} | ${esc(t.usage||t.triggerDescription)}</span></button>`).join('');}
async function selectWhatsAppTemplate(key,force=false){const t=(S.waTemplates||[]).find(x=>x.key===key);if(!t||!force&&t.key===S.waSelected&&S.waHistoryIndex>=0)return;if(!force&&hasUnsavedWhatsAppChanges()&&!await askAdminConfirm('Ha alteracoes nao salvas nesta mensagem. Deseja trocar de modelo e descarta-las?'))return;S.waSelected=t.key;renderWhatsAppTemplateList();setText('wa-title',t.title);setText('wa-audience',t.audience);setText('wa-trigger',t.triggerDescription);setText('wa-updated',fmtDateTime(t.updatedAt));setText('wa-usage',t.usage||'');const keyEl=document.getElementById('wa-key');if(keyEl)keyEl.textContent=t.key;const body=getWaBody();const value=t.body||'';if(body)body.value=value;S.waOriginalBody=value;S.waHistory=[value];S.waHistoryIndex=0;const vars=document.getElementById('wa-vars');if(vars)vars.innerHTML=(t.variables||[]).map(v=>`<button class="var-chip" data-admin-click="insert-whatsapp-variable" data-variable="${esc(v)}">{{${esc(v)}}}</button>`).join('');renderWhatsAppPreview(value);}
function waEscapeHtml(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');}
function renderWhatsAppMarkup(text){let out=waEscapeHtml(text);out=out.replace(/```([\s\S]+?)```/g,'<code>$1</code>');out=out.replace(/\*([^*\n][^*]*?)\*/g,'<strong>$1</strong>');out=out.replace(/_([^_\n][^_]*?)_/g,'<em>$1</em>');out=out.replace(/~([^~\n][^~]*?)~/g,'<s>$1</s>');return out;}
function renderWhatsAppPreview(body){let out=body||'';Object.keys(WA_SAMPLE).forEach(k=>{out=out.replaceAll('{{'+k+'}}',WA_SAMPLE[k]);});const preview=document.getElementById('wa-preview');if(preview)preview.innerHTML=renderWhatsAppMarkup(out);const count=document.getElementById('wa-count');if(count)count.textContent=(body||'').length+' caracteres';}
function recordWhatsAppHistory(value){if(S.waApplyingHistory)return;if(S.waHistory[S.waHistoryIndex]===value)return;S.waHistory=S.waHistory.slice(0,S.waHistoryIndex+1);S.waHistory.push(value);S.waHistoryIndex=S.waHistory.length-1;}
function applyWhatsAppHistory(index){const body=getWaBody();if(!body||index<0||index>=S.waHistory.length)return;S.waApplyingHistory=true;S.waHistoryIndex=index;body.value=S.waHistory[index];body.focus();body.selectionStart=body.selectionEnd=body.value.length;renderWhatsAppPreview(body.value);S.waApplyingHistory=false;}
function hasUnsavedWhatsAppChanges(){return S.view==='notificacoes'&&S.waSelected!==null&&(getWaBody()?.value||'')!==S.waOriginalBody;}
function insertAtCursor(text,wrapEnd){const body=getWaBody();if(!body)return;const start=body.selectionStart||0;const end=body.selectionEnd||0;const selected=body.value.slice(start,end);const finalText=wrapEnd!==undefined?text+(selected||'texto')+wrapEnd:text;body.value=body.value.slice(0,start)+finalText+body.value.slice(end);body.focus();const selectedLength=selected?selected.length:5;const cursor=wrapEnd!==undefined?start+text.length+selectedLength:start+finalText.length;body.selectionStart=body.selectionEnd=cursor;recordWhatsAppHistory(body.value);renderWhatsAppPreview(body.value);}
function insertWhatsAppVariable(name){insertAtCursor('{{'+name+'}}');}
function formatWhatsApp(kind){const map={bold:['*','*'],italic:['_','_'],strike:['~','~'],mono:['```','```']};const pair=map[kind];if(pair)insertAtCursor(pair[0],pair[1]);}
function toggleEmojiPanel(){const panel=document.getElementById('emoji-panel');if(!panel)return;if(!panel.innerHTML)panel.innerHTML=WA_EMOJIS.map(e=>`<button type="button" class="emoji-choice" data-emoji="${e}">${e}</button>`).join('');panel.classList.toggle('open');}
function insertEmoji(emoji){insertAtCursor(emoji);document.getElementById('emoji-panel')?.classList.remove('open');}
function wireWhatsAppEditor(){if(S.waEditorWired)return;S.waEditorWired=true;document.querySelectorAll('[data-wa-format]').forEach(btn=>{btn.addEventListener('click',()=>formatWhatsApp(btn.dataset.waFormat));});const emojiToggle=document.getElementById('wa-emoji-toggle');if(emojiToggle)emojiToggle.addEventListener('click',toggleEmojiPanel);const panel=document.getElementById('emoji-panel');if(panel)panel.addEventListener('click',e=>{const btn=e.target.closest('[data-emoji]');if(btn)insertEmoji(btn.dataset.emoji);});const body=getWaBody();if(body){body.addEventListener('input',()=>recordWhatsAppHistory(body.value));body.addEventListener('keydown',e=>{if(!(e.ctrlKey||e.metaKey))return;const undo=e.key.toLowerCase()==='z'&&!e.shiftKey;const redo=e.key.toLowerCase()==='y'||e.key.toLowerCase()==='z'&&e.shiftKey;if(undo&&S.waHistoryIndex>0){e.preventDefault();applyWhatsAppHistory(S.waHistoryIndex-1);}else if(redo&&S.waHistoryIndex<S.waHistory.length-1){e.preventDefault();applyWhatsAppHistory(S.waHistoryIndex+1);}});}window.addEventListener('beforeunload',e=>{if(!hasUnsavedWhatsAppChanges())return;e.preventDefault();e.returnValue='';});}
window.formatWhatsApp=formatWhatsApp;window.toggleEmojiPanel=toggleEmojiPanel;window.insertEmoji=insertEmoji;
async function saveWhatsAppTemplate(){if(!S.waSelected)return;const body=getWaBody()?.value||'';const r=await fetch(API+'/whatsapp/templates/'+encodeURIComponent(S.waSelected),{method:'PUT',headers:hdrs(),body:JSON.stringify({Body:body})});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao salvar mensagem.');return;}const idx=S.waTemplates.findIndex(x=>x.key===S.waSelected);if(idx>=0)S.waTemplates[idx]=data.template;showAdminMessage('success',data.msg||'Mensagem atualizada.');selectWhatsAppTemplate(S.waSelected,true);}
async function resetWhatsAppTemplate(){if(!S.waSelected)return;if(!await askAdminConfirm('Restaurar esta mensagem para o texto padrao?'))return;const r=await fetch(API+'/whatsapp/templates/'+encodeURIComponent(S.waSelected)+'/reset',{method:'POST',headers:hdrs()});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao restaurar mensagem.');return;}const idx=S.waTemplates.findIndex(x=>x.key===S.waSelected);if(idx>=0)S.waTemplates[idx]=data.template;showAdminMessage('success',data.msg||'Mensagem restaurada.');selectWhatsAppTemplate(S.waSelected,true);}
function openNewWhatsAppTemplate(){document.getElementById('wa-new-title').value='';document.getElementById('wa-new-audience').value='Personalizada';document.getElementById('wa-new-trigger').value='Mensagem personalizada criada no painel. Para envio automatico, vincule esta chave no backend.';document.getElementById('wa-new-body').value='';document.getElementById('modal-wa-template').classList.add('active');}
async function createWhatsAppTemplate(){const payload={Title:document.getElementById('wa-new-title').value.trim(),Audience:document.getElementById('wa-new-audience').value.trim(),TriggerDescription:document.getElementById('wa-new-trigger').value.trim(),Body:document.getElementById('wa-new-body').value};const r=await fetch(API+'/whatsapp/templates',{method:'POST',headers:hdrs(),body:JSON.stringify(payload)});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao criar mensagem.');return;}S.waTemplates.push(data.template);S.waSelected=data.template.key;closeModals();showAdminMessage('success',data.msg||'Mensagem criada.');renderWhatsAppTemplateList();selectWhatsAppTemplate(S.waSelected,true);}
function fmtDateTime(d){if(!d)return'--';const dt=new Date(d);if(isNaN(dt))return'--';return dt.toLocaleDateString('pt-BR',{day:'2-digit',month:'2-digit',year:'numeric'})+' '+dt.toLocaleTimeString('pt-BR',{hour:'2-digit',minute:'2-digit'});}
function fmtCur(v){if(v===null||v===undefined)return'&#8212;';return'R$ '+parseFloat(v).toFixed(2).replace('.',',').replace(/\B(?=(\d{3})+(?!\d))/g,'.');}
function fmtDate(d){if(!d)return'&#8212;';const dt=new Date(d);if(isNaN(dt))return'&#8212;';return dt.toLocaleDateString('pt-BR',{day:'2-digit',month:'2-digit',year:'numeric'});}
function sbadge(status,isActive){
  if(status==='pago'&&isActive)return'<span class="badge b-ok">&#10003; Ativa</span>';
  if(status==='pago'&&!isActive)return'<span class="badge b-muted">Expirada</span>';
  if(status==='pendente')return'<span class="badge b-warn">&#9203; Pendente</span>';
  if(status==='cancelado')return'<span class="badge b-err">&#10005; Cancelado</span>';
  if(status==='expirado')return'<span class="badge b-muted">Expirado</span>';
  return'<span class="badge b-muted">'+status+'</span>';
}
function esc(s){if(!s)return'';return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}

async function loadAdminPartials(){
  const target=document.getElementById('admin-modals-root');
  if(!target)return;
  try{const r=await fetch('/admin/partials/modals',{cache:'no-store'});if(r.ok){target.innerHTML=await r.text();enhanceAdminModals();}}
  catch(e){showAdminMessage('error','Nao foi possivel carregar os modais do admin.');}
}
const adminPartialsPromise=loadAdminPartials();
init();
