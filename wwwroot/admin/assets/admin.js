let _allLocalUsers = []; // Para edi&ccedil;&atilde;o
    let _adComputers = [];
    let _adAccessUser = '';
    let _adSelectedComputers = new Set();
    let _adGroups = [];
    let _adComputerGroups = {};
    let _adPendingGroupSelection = null;
    let _adLinkUsers = [];
    let _adLinkOptionsLoaded = false;
    let adminToastTimer = null;
    let adminConfirmResolver = null;

    function showAdminMessage(type, message) {
        const toast = document.getElementById('adminToast');
        if (!toast) return;
        toast.textContent = '';

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

    function askAdminConfirm(message) {
        const modal = document.getElementById('modal-confirm');
        const msg = document.getElementById('confirm-message');
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
        document.getElementById('m-ad-edit-whatsapp').value = '';
        document.getElementById('m-ad-edit-password').value = '';
        document.getElementById('m-ad-edit-active').checked = false;
        document.getElementById('m-ad-edit-never-expires').checked = false;
        document.getElementById('modal-ad-edit').classList.add('active');

        const u = await apiFetch('/api/admin/ad/users/' + encodeURIComponent(username));
        if (u) {
            document.getElementById('m-ad-edit-fullname').value = u.fullName || '';
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
    }

    async function openManualOrderModal() {
        if (!_allLocalUsers || !_allLocalUsers.length) {
            if (document.getElementById('users-body')) await loadUsers();
            else await loadLocalUsersSnapshot();
        }
        const sel = document.getElementById('m-order-user');
        sel.innerHTML = '<option value="">-- Selecione o Cliente --</option>' + 
            (_allLocalUsers || []).map(u => `<option value="${u.id}">${esc(u.name)} (${esc(u.email)})</option>`).join('');
        
        document.getElementById('m-order-pcs').value = 1;
        document.getElementById('m-order-slots').value = 4;
        document.getElementById('m-order-price').value = "0.00";
        document.getElementById('m-order-desc').value = "";
        
        document.getElementById('manualOrderModal').classList.add('active');
    }

    async function saveManualOrder() {
        const userId = document.getElementById('m-order-user').value;
        if (!userId) return showAdminMessage('error', 'Selecione um cliente.');

        const req = {
            userId,
            period: document.getElementById('m-order-period').value,
            days: parseInt(document.getElementById('m-order-days').value) || 7,
            computers: parseInt(document.getElementById('m-order-pcs').value) || 1,
            wydsPerComputer: parseInt(document.getElementById('m-order-slots').value) || 4,
            price: parseFloat(document.getElementById('m-order-price').value) || 0,
            description: document.getElementById('m-order-desc').value.trim()
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
        const data = await apiFetch(API + '/users?page=1&limit=500&search=');
        _allLocalUsers = data?.users || [];
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
    
    async function deleteOrder(id) {
        if(await askAdminConfirm('Tem certeza que deseja excluir este pedido cancelado permanentemente?')) {
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
            _adLinkUsers = users
                .filter(x => x.ouPath === 'USUARIOS')
                .sort((a, b) => String(a.username || '').localeCompare(String(b.username || '')));
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
            menu.innerHTML = '<div class="ad-link-empty">Nenhum usu&aacute;rio da pasta USUARIOS encontrado.</div>';
        } else {
            menu.innerHTML = list.map(x => {
                const label = x.fullName || x.username;
                return `<button type="button" class="ad-link-option" data-username="${esc(x.username)}" onclick="selectAdLinkUser(this.dataset.username)">
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
        document.getElementById('btn-cancel-no-refund').textContent = isPaid ? 'Cancelar SEM Reembolso' : 'Cancelar';
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
                <button class="btn btn-outline" onclick="closeModals()">Cancelar</button>
                <button class="btn btn-outline" style="color:var(--txt);border-color:var(--border)" onclick="executeDeleteUser(false)">Excluir apenas do Site</button>
                <button class="btn btn-outline" style="color:var(--err);border-color:var(--err)" onclick="executeDeleteUser(true)">Excluir do Site e AD</button>
            `;
        } else {
            footer.innerHTML = `
                <button class="btn btn-outline" onclick="closeModals()">Cancelar</button>
                <button class="btn btn-outline" style="color:var(--err);border-color:var(--err)" onclick="executeDeleteUser(false)">Excluir Usuário</button>
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
        btn.classList.add('active');
        document.getElementById('ad-users-tbl').classList.add('hidden');
        document.getElementById('ad-website-tbl').classList.add('hidden');
        document.getElementById('ad-expired-tbl').classList.add('hidden');
        document.getElementById('ad-groups-tbl').classList.add('hidden');
        document.getElementById('ad-computers-tbl').classList.add('hidden');
        document.getElementById('ad-'+f+'-tbl').classList.remove('hidden');
    }

    async function loadAd() {
        const sts = document.getElementById('ad-status');
        const cont = document.getElementById('ad-content');
        
        sts.innerHTML = 'Conectando ao servidor 172.31.2.3...';
        const r = await apiFetch('/api/admin/ad/status');
        if(!r || !r.online) {
            sts.innerHTML = '<span style="color:red">Desconectado</span>';
            document.getElementById('ad-actions').classList.add('hidden');
            cont.innerHTML = `<div class="offline-banner">
                <span style="font-size:40px;margin-bottom:10px;">&#128268;</span>
                <h3 style="font-size:18px;font-weight:bold;color:#f87171;">Servidor Active Directory Desligado</h3>
                <p style="color:var(--txt2);max-width:400px;margin-top:10px;">O servidor Windows Server (172.31.2.3) n&atilde;o respondeu no porto 389. Esta vis&atilde;o est&aacute; restrita para economia de energia.</p>
                <button class="btn btn-outline" style="margin-top:20px;" onclick="loadAd()">&#128260; Tentar Novamente</button>
            </div>`;
            cont.classList.remove('hidden');
            return;
        }

        sts.innerHTML = '<span style="color:#4ade80">Online (172.31.2.3)</span>';
        document.getElementById('ad-actions').classList.remove('hidden');
        
        // Restore layout if it was overwritten by offline banner
        if(cont.innerHTML.includes('offline-banner')) {
            cont.innerHTML = `
                <div class="filter-bar" id="ad-fbar">
                    <button class="fb active" onclick="setAdFilter('users',this)">Usu&aacute;rios Ativos</button>
                    <button class="fb" onclick="setAdFilter('website',this)">Website Users</button>
                    <button class="fb" onclick="setAdFilter('expired',this)">Usu&aacute;rios Expirados</button>
                    <button class="fb" onclick="setAdFilter('groups',this)">Grupos</button>
                    <button class="fb" onclick="setAdFilter('computers',this)">Computadores</button>
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
                    <div class="tbl-wrap"><table><thead><tr><th>Grupo</th><th>Descri&ccedil;&atilde;o</th></tr></thead><tbody id="ad-groups-body"></tbody></table></div>
                </div>
                <div class="tbl-card hidden" id="ad-computers-tbl">
                    <div class="tbl-wrap"><table><thead><tr><th>Computador</th><th>Descri&ccedil;&atilde;o</th><th>Sistema Operacional</th><th>Status</th></tr></thead><tbody id="ad-computers-body"></tbody></table></div>
                </div>
            `;
        }
        
        cont.classList.remove('hidden');

        // Load computers first because the access modal uses this list.
        const cres = await apiFetch('/api/admin/ad/computers');
        _adComputers = Array.isArray(cres) ? cres : [];
        if(cres) {
            const sortedCres = [...cres].sort((a,b) => String(a.description||a.name||'').localeCompare(String(b.description||b.name||''), 'pt-BR', {sensitivity:'base'}));
            document.getElementById('ad-computers-body').innerHTML = sortedCres.map(c=>`<tr><td>${esc(c.name)}</td><td>${esc(c.description||'-')}</td><td>${esc(c.operatingSystem||'-')}</td><td>${c.isActive!==false?'<span class="badge b-ok">Ativo</span>':'<span class="badge b-muted">Inativo</span>'}</td></tr>`).join('');
        }

        // Load users
        const ures = await apiFetch('/api/admin/ad/users');
        if(ures) {
            const sortedUsers = [...ures].sort((a,b) =>
                String(a.fullName || a.username || '').localeCompare(
                    String(b.fullName || b.username || ''),
                    'pt-BR',
                    {sensitivity:'base'}
                )
            );
            const ativos = sortedUsers.filter(x => x.ouPath === 'USUARIOS');
            const website = sortedUsers.filter(x => x.ouPath === 'USUARIOS_WEBSITE');
            const expirados = sortedUsers.filter(x => x.ouPath === 'USUARIOS_EXPIRADOS');

            const renderUser = (u) => {
                const computers = Array.isArray(u.computers) ? u.computers : [];
                const computersCsv = computers.join(',');
                // Se allowAllComputers=true, sem restrição (campo vazio no AD) = acesso a todos
                const computerText = u.allowAllComputers
                    ? '<span style="color:#60a5fa">&#127760; Todos os computadores</span>'
                    : (computers.length > 0 ? computers.map(esc).join(', ') : 'Nenhum PC');
                const expiresValue = u.expiresAt ? u.expiresAt.substring(0,10) : '';
                return `<tr>
                    <td>${esc(u.username)}</td><td>${esc(u.fullName)}</td>
                    <td>${u.isActive?'<span style="color:#4ade80">Ativo</span>':'<span style="color:red">Desativado</span>'}</td>
                    <td style="max-width:240px">
                        <button class="btn btn-outline" style="padding:4px 8px;font-size:10px" onclick="openAdAccessModal('${u.username}', '${computersCsv}', ${u.allowAllComputers})">&#128187; Gerenciar Acessos</button>
                        <div style="font-size:10px;margin-top:4px;color:var(--txt2);white-space:normal">${computerText}</div>
                    </td>
                    <td>
                        <div class="date-tools">
                            <label class="inline-check"><input type="checkbox" id="never_${u.username}" ${expiresValue?'':'checked'} onchange="toggleAdNever('${u.username}')"> Nunca</label>
                            <input type="date" id="exp_${u.username}" value="${expiresValue}" ${expiresValue?'':'disabled'}>
                            <button class="btn btn-outline" style="padding:4px 8px;font-size:10px" onclick="setAdExpire('${u.username}')">&#10004;</button>
                        </div>
                    </td>
                    <td>
                        <div class="action-row">
                            <button class="btn btn-outline" style="padding:4px 8px;font-size:10px;color:#3b82f6;border-color:#3b82f6" onclick="openAdEditModal('${u.username}')">&#9998; Editar</button>
                            <button class="btn btn-outline" style="padding:4px 8px;font-size:10px;color:#60a5fa;border-color:#60a5fa" onclick="openAdPasswordModal('${u.username}')">&#128274; Senha</button>
                            <button class="btn btn-outline" style="padding:4px 8px;font-size:10px;color:#a78bfa;border-color:#a78bfa" onclick="openDuplicateModal('${u.username}', '${esc(u.fullName)}')">&#128203; Duplicar</button>
                            ${u.ouPath === 'USUARIOS'
                                ? `<button class="btn btn-outline" style="padding:4px;font-size:10px;color:#fbbf24;border-color:#fbbf24" onclick="moveOu('${u.username}', true)">Arquivar</button>`
                                : `<button class="btn btn-outline" style="padding:4px;font-size:10px;color:#4ade80;border-color:#4ade80" onclick="moveOu('${u.username}', false)">${u.ouPath === 'USUARIOS_WEBSITE' ? 'Mover para ativos' : 'Reativar'}</button>`}
                            <button class="btn btn-outline" style="padding:4px;font-size:10px;color:red;border-color:red" onclick="deleteAdUser('${u.username}')">Excluir</button>
                        </div>
                    </td>
                </tr>`;
            };

            document.getElementById('ad-users-body').innerHTML = ativos.map(renderUser).join('');
            document.getElementById('ad-website-body').innerHTML = website.map(renderUser).join('');
            document.getElementById('ad-expired-body').innerHTML = expirados.map(renderUser).join('');
        }

        // Load groups
        const gres = await apiFetch('/api/admin/ad/groups');
        _adGroups = Array.isArray(gres)
            ? [...gres].sort((a,b) => String(a.name||'').localeCompare(String(b.name||''), 'pt-BR', {sensitivity:'base'}))
            : [];
        if(gres) document.getElementById('ad-groups-body').innerHTML = _adGroups.map(g=>`<tr><td>${esc(g.name)}</td><td>${esc(g.description||'-')}</td></tr>`).join('');

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
                const sa = _adSelectedComputers.has(normalizeComputerName(a.name)) ? 0 : 1;
                const sb = _adSelectedComputers.has(normalizeComputerName(b.name)) ? 0 : 1;
                // Selecionados primeiro, depois ordena por descrição
                return sa - sb || String(a.description||a.name||'').localeCompare(String(b.description||b.name||''), 'pt-BR', {sensitivity:'base'});
            });

        if(!list.length) {
            holder.innerHTML = '<div class="empty">Nenhum computador ativo encontrado.</div>';
            return;
        }

        let html = `<label class="check-row" style="margin-bottom:12px; border-bottom:1px solid var(--border); padding-bottom:12px; display:block">
            <input type="checkbox" id="ad-check-all" onchange="toggleAdAllComputers()" ${ _adAllowAllComputers ? 'checked' : '' }>
            <span style="color:#60a5fa;font-weight:600;margin-left:8px">&#127760; Liberar todos os computadores</span>
        </label>`;

        html += list.map(c => {
            const name = normalizeComputerName(c.name);
            const checked = _adSelectedComputers.has(name) ? 'checked' : '';
            const desc = c.description || c.operatingSystem || 'Sem descricao';
            return `<label class="check-row">
                <input type="checkbox" value="${esc(name)}" ${checked} onchange="toggleAdComputer(this)" class="ad-comp-cb">
                <div class="check-main"><div class="check-name">${esc(name)}</div><div class="check-desc">${esc(desc)}</div></div>
                ${c.isActive!==false?'<span class="badge b-ok">Ativo</span>':'<span class="badge b-muted">Inativo</span>'}
            </label>`;
        }).join('');
        
        holder.innerHTML = html;
        toggleAdAllComputers(); // Update disabled state initially
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
            `Selecione o grupo que deve ser usado para ${action} o acesso do computador ${data.computer}.${suggestion}`;

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

const ADMIN_ROUTES={dashboard:'/admin/dashboard.html',financeiro:'/admin/financeiro.html',crm:'/admin/crm.html',pedidos:'/admin/pedidos.html',usuarios:'/admin/usuarios.html',ad:'/admin/active-directory.html',notificacoes:'/admin/notificacoes.html'};
const S={token:null,user:null,view:document.body?.dataset.view||'dashboard',ordFilter:'all',ordPage:1,usrPage:1,usrSearch:'',chart:null,statusChart:null,dashData:null,dashPeriod:localStorage.getItem('premierAdminDashPeriod')||'month',waTemplates:[],waSelected:null};
const API='/api/admin';

function init(){
  try{const d=JSON.parse(localStorage.getItem('premierAdmin')||'{}');if(d.token){S.token=d.token;S.user=d.user;showApp();return;}}catch(e){}
  showLogin();
}
function showLogin(){document.getElementById('login-screen').style.display='flex';document.getElementById('app').style.display='none';}
function showApp(){
  document.getElementById('login-screen').style.display='none';document.getElementById('app').style.display='flex';
  const n=S.user?.name||'Admin';document.getElementById('sname').textContent=n;document.getElementById('savatar').textContent=n[0].toUpperCase();
  setupCurrentView();
  loadCurrentView();
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
    S.token=d.token;S.user=d.user;
    localStorage.setItem('premierAdmin',JSON.stringify({token:d.token,user:d.user}));
    showApp();
  }catch(e){if(window.turnstile)window.turnstile.reset();showErr('Erro de conexao. Tente novamente.');}
  finally{btn.disabled=false;btn.innerHTML='Entrar no Painel';}
  function showErr(m){err.textContent=m;err.style.display='block';}
}
document.addEventListener('keydown',e=>{if(e.key==='Enter'&&document.getElementById('login-screen').style.display!=='none')doLogin();});
document.addEventListener('click',e=>{const p=document.getElementById('ad-link-picker');if(p&&!p.contains(e.target))closeAdLinkDropdown();});
function doLogout(){localStorage.removeItem('premierAdmin');S.token=null;S.user=null;if(S.chart){S.chart.destroy();S.chart=null;}if(S.statusChart){S.statusChart.destroy();S.statusChart=null;}showLogin();}
function hdrs(){return{'X-Session-Token':S.token,'Content-Type':'application/json'};}
async function apiFetch(url){
  const r=await fetch(url,{headers:hdrs()});
  if(r.status===401){doLogout();return null;}
  const data=await r.json().catch(()=>null);
  if(!r.ok){showAdminMessage('error', data?.erro||'Erro ao carregar dados.');return null;}
  return data;
}

const HDR={dashboard:['Dashboard','Cockpit financeiro e operacional'],financeiro:['Financeiro','Receita, planos e origem dos pedidos'],crm:['CRM','Clientes, renovacoes e fila comercial'],pedidos:['Pedidos','Gerenciar todos os pedidos'],usuarios:['Usuarios','Gerenciar usuarios cadastrados'],ad:['Active Directory','Gerenciar AD Local'],notificacoes:['Notificacoes','Mensagens automaticas do WhatsApp']};
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
  if(S.view==='dashboard')loadDash();else if(S.view==='financeiro')loadFinanceiro();else if(S.view==='crm')loadCrm();else if(S.view==='pedidos')loadOrders();else if(S.view==='usuarios')loadUsers();else if(S.view==='ad')loadAd();else if(S.view==='notificacoes'){wireWhatsAppEditor();loadNotificacoes();}
}
function go(v){
  const target=ADMIN_ROUTES[v];
  if(target&&window.location.pathname!==target){window.location.href=target;return;}
  S.view=v;setupCurrentView();loadCurrentView();
}
function refresh(){S.dashData=null;loadCurrentView();}

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
function handlePeriodChange(){
  const periodSelect=document.getElementById('dash-period');
  if(periodSelect&&periodSelect.value!==S.dashPeriod)periodSelect.value=S.dashPeriod;
  const custom=periodSelect?.value==='custom';
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
  handlePeriodChange();
  const data=await ensureDashData();if(!data)return;
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
  renderChart(data.revenueSeries||data.monthlyRevenue||[]);
  renderStatusChart(data.statusBreakdown||[]);
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
  rb.innerHTML=rows.map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${esc(o.period)}</td><td>${o.computers}PC/${o.wydsPerComputer}sl</td><td style="font-weight:600">${fmtCur(o.totalPrice)}</td><td>${sbadge(o.status,o.isActive)}</td><td>${o.isActive?'<span style="color:var(--ok);font-size:12px">'+fmtDate(o.expiresAt)+'</span>':'<span class="muted">'+fmtDate(o.expiresAt)+'</span>'}</td><td class="muted">${fmtDate(o.createdAt)}</td></tr>`).join('');
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
  if(body)body.innerHTML=(data.statusBreakdown||[]).length?(data.statusBreakdown||[]).map(x=>`<tr><td>${sbadge(x.status,x.status==='pago')}</td><td>${x.count}</td><td style="font-weight:600">${fmtCur(x.revenue)}</td></tr>`).join(''):'<tr><td colspan="3" class="empty">Sem pedidos no periodo.</td></tr>';
}
function renderCrm(data){
  const st=data.stats||{};
  setText('crm-active',st.activeCustomers||0);
  setText('crm-expiring',st.expiringSoon||0);
  setText('crm-delivery',st.pendingDeliveryOrders||0);
  setText('crm-new',st.newUsers||0);
  renderActionList('crm-actions',data.actionQueue||[]);
  const upcoming=document.getElementById('upcoming-body');
  if(upcoming)upcoming.innerHTML=(data.upcomingExpirations||[]).length?(data.upcomingExpirations||[]).map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${esc(o.period)} <span class="muted">${o.computers}PC/${o.wydsPerComputer}sl</span></td><td style="font-weight:600">${fmtCur(o.totalPrice)}</td><td>${daysUntil(o.expiresAt)}</td><td class="muted">${esc(o.whatsapp||'-')}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">Nenhuma licenca ativa encontrada.</td></tr>';
  const top=document.getElementById('top-customers-body');
  if(top)top.innerHTML=(data.topCustomers||[]).length?(data.topCustomers||[]).map(o=>`<tr><td><div class="ucell"><div class="avatar">${initial(o.userName)}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td>${o.orders}</td><td style="font-weight:600;color:var(--ok)">${fmtCur(o.revenue)}</td><td class="muted">${fmtDate(o.lastOrderAt)}</td><td class="muted">${esc(o.whatsapp||'-')}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">Sem clientes pagantes neste periodo.</td></tr>';
}
function renderBreakdownList(id,rows,title,sub,value){
  const el=document.getElementById(id);if(!el)return;
  if(!rows.length){el.innerHTML='<div class="empty">Sem dados no periodo.</div>';return;}
  const max=Math.max(...rows.map(x=>parseFloat(x.revenue||x.count||0)),1);
  el.innerHTML=rows.map(x=>{const v=parseFloat(x.revenue||x.count||0);return `<div class="insight-item"><div class="insight-main"><div class="insight-title">${title(x)}</div><div class="insight-sub">${sub(x)}</div><div class="progress"><span style="width:${Math.max(6,Math.round(v/max*100))}%"></span></div></div><div class="insight-val">${value(x)}</div></div>`;}).join('');
}
function renderActionList(id,rows){
  const el=document.getElementById(id);if(!el)return;
  if(!rows.length){el.innerHTML='<div class="empty">Nenhuma acao urgente agora.</div>';return;}
  el.innerHTML=rows.map(x=>`<div class="insight-item"><div class="insight-main"><div class="kpi-note"><span class="action-chip">${esc(x.type)}</span><span class="muted">${fmtDate(x.eventAt)}</span></div><div class="insight-title" style="margin-top:6px">${esc(x.userName)}</div><div class="insight-sub">${esc(x.email)}${x.whatsapp?' | '+esc(x.whatsapp):''}</div></div><div class="insight-val">${fmtCur(x.totalPrice)}</div></div>`).join('');
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

async function loadOrders(p){
  if(p)S.ordPage=p;
  const body=document.getElementById('orders-body');body.innerHTML='<tr><td colspan="12" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const data=await apiFetch(`${API}/orders?status=${S.ordFilter}&page=${S.ordPage}&limit=20`);if(!data)return;
  if(!data.orders?.length){body.innerHTML='<tr><td colspan="12" class="empty">Nenhum pedido encontrado.</td></tr>';}
  else{body.innerHTML=data.orders.map(o=>`<tr><td><div class="ucell"><div class="avatar">${esc(o.userName)[0].toUpperCase()}</div><div><div class="ucell-name">${esc(o.userName)}</div><div class="ucell-email">${esc(o.email)}</div></div></div></td><td class="muted">${esc(o.whatsapp||'&#8212;')}</td><td>${esc(o.period)} <span class="muted">(${o.days}d)</span></td><td>${o.computers}PC/${o.wydsPerComputer}sl</td><td style="font-weight:600">${fmtCur(o.totalPrice)}</td><td class="muted" style="font-family:monospace;font-size:11px">${esc(o.asaasPaymentId||'-')}${o.paidManually?'<div style="font-family:Inter,sans-serif;font-size:10px;color:var(--ok);margin-top:3px">Pago manual</div>':''}</td><td>${sbadge(o.status,o.isActive)}</td><td><label class="inline-check"><input type="checkbox" ${o.status!=='pago'?'disabled':''} ${o.delivered?'checked':''} onchange="toggleOrderDelivery('${o.id}', this.checked, this)"><span ${o.status!=='pago'?'class="muted"':''}>${o.delivered?'Entregue':'Pendente'}</span></label></td><td>${o.isActive?'<span style="color:var(--ok);font-size:12px">Ativa ate '+fmtDate(o.expiresAt)+'</span>':'<span class="muted">Exp. '+fmtDate(o.expiresAt)+'</span>'}</td><td class="muted">${fmtDate(o.createdAt)}</td><td>${o.canceledAt?'<span class="muted" style="color:var(--err)">'+(o.paidManually||(o.asaasPaymentId||'').startsWith('MANUAL_')?'':(o.refunded?'C/R ':'S/R '))+fmtDate(o.canceledAt)+'</span>':'<span class="muted">&#8212;</span>'}</td><td><div class="action-row">${(o.status==='pendente'||o.status==='expirado')?`<button class="btn btn-outline" style="color:var(--ok);border-color:var(--ok);padding:4px 8px;font-size:11px;margin-right:4px;" onclick="markOrderPaid('${o.id}')">Marcar Pago</button>`:''}${(o.status==='pago'||o.status==='pendente')?`<button class="btn btn-outline" style="color:var(--err);border-color:var(--err);padding:4px 8px;font-size:11px" onclick="openCancelOrderModal('${o.id}', ${o.status==='pago' && !o.paidManually && !(o.asaasPaymentId||'').startsWith('MANUAL_')})">Cancelar</button>`:''}${o.status==='cancelado'?`<button class="btn btn-outline" style="padding:4px 8px;font-size:11px" onclick="deleteOrder('${o.id}')" title="Excluir">&#128465;</button>`:''}</div></td></tr>`).join('');}
  renderPag('orders',data.total,S.ordPage,20);
}
function setFilter(f,btn){S.ordFilter=f;S.ordPage=1;document.querySelectorAll('#fbar .fb').forEach(b=>b.classList.remove('active'));btn.classList.add('active');loadOrders();}

async function loadUsers(p){
  if(p)S.usrPage=p;
  const body=document.getElementById('users-body');body.innerHTML='<tr><td colspan="10" class="loading"><div class="spinner"></div> Carregando...</td></tr>';
  const data=await apiFetch(`${API}/users?page=${S.usrPage}&limit=20&search=${encodeURIComponent(S.usrSearch)}`);if(!data)return;
  if(!data.users?.length){body.innerHTML='<tr><td colspan="10" class="empty">Nenhum usuario encontrado.</td></tr>';}
  else {
      _allLocalUsers = data.users;
      body.innerHTML=data.users.map(u=>`<tr>
          <td><div class="ucell"><div class="avatar avatar-lg">${esc(u.name)[0].toUpperCase()}</div><div><div class="ucell-name">${esc(u.name)}</div><div class="ucell-email">${esc(u.email)}</div></div></div></td>
          <td class="muted">${esc(u.whatsapp||'-')}</td>
          <td>${u.isActive?'<span class="badge b-ok">Ativa</span>':'<span class="badge b-err">Inativa</span>'}</td>
          <td><button class="btn btn-outline" style="padding:4px 8px;font-size:11px" onclick="openAdLinkModal('${u.id}')">${u.adUsername?esc(u.adUsername):'Vincular'}</button></td>
          <td style="text-align:center;font-weight:500">${u.totalOrders}</td>
          <td style="font-weight:600;color:var(--ok)">${fmtCur(u.totalSpent)}</td>
          <td style="text-align:center">${u.activeLicenses>0?'<span class="badge b-ok">'+u.activeLicenses+' ativa'+(u.activeLicenses>1?'s':'')+'</span>':'<span class="muted">&#8212;</span>'}</td>
          <td class="muted">${fmtDate(u.createdAt)}</td>
          <td><label class="inline-check"><input type="checkbox" data-user-id="${u.id}" ${u.emailConfirmed?'checked disabled':'onchange="confirmEmailFromCheckbox(this)"'}><span>${u.emailConfirmed?'Confirmado':'Validar'}</span></label></td>
          <td>
              <div class="action-row">
                  <button class="btn btn-outline" style="padding:4px 8px;font-size:12px" title="Editar" onclick="openUserModal('${u.id}')">&#9998;</button>
                  <button class="btn btn-outline" style="padding:4px 8px;font-size:12px;color:${u.isActive?'var(--warn)':'var(--ok)'};border-color:${u.isActive?'var(--warn)':'var(--ok)'}" title="${u.id === S.user?.id ? 'Voc&ecirc; n&atilde;o pode inativar sua pr&oacute;pria conta' : (u.isActive?'Inativar cadastro':'Ativar cadastro')}" ${u.id === S.user?.id && u.isActive ? 'disabled' : `onclick="toggleLocalUser('${u.id}', ${u.isActive?'false':'true'}, this)"`}>${u.isActive?'Inativar':'Ativar'}</button>
                  <button class="btn btn-outline" style="padding:4px 8px;font-size:12px;color:var(--err);border-color:var(--err)" title="Excluir" onclick="deleteLocalUser('${u.id}', ${u.adUsername ? 'true' : 'false'})">&#128465;</button>
              </div>
          </td>
      </tr>`).join('');
  }
  renderPag('users',data.total,S.usrPage,20);
}
let st_=null;function handleSearch(v){S.usrSearch=v;S.usrPage=1;clearTimeout(st_);st_=setTimeout(()=>loadUsers(),400);}

function renderPag(type,total,cur,limit){
  const pages=Math.ceil(total/limit);const cnt=document.getElementById(type+'-count');const btns=document.getElementById(type+'-pag');
  if(total>0){const s=(cur-1)*limit+1;const e=Math.min(cur*limit,total);cnt.textContent=s+'-'+e+' de '+total;}else{cnt.textContent='0 resultados';}
  if(pages<=1){btns.innerHTML='';return;}
  const fn=type==='orders'?'loadOrders':'loadUsers';
  let h=`<button class="pb" onclick="${fn}(${cur-1})" ${cur===1?'disabled':''}>&lsaquo;</button>`;
  for(let i=1;i<=pages;i++){if(i===1||i===pages||(i>=cur-1&&i<=cur+1))h+=`<button class="pb ${i===cur?'active':''}" onclick="${fn}(${i})">${i}</button>`;else if(i===cur-2||i===cur+2)h+=`<span style="color:var(--txt2);line-height:30px;padding:0 2px">&hellip;</span>`;}
  h+=`<button class="pb" onclick="${fn}(${cur+1})" ${cur===pages?'disabled':''}>&rsaquo;</button>`;
  btns.innerHTML=h;
}

const WA_SAMPLE={cliente_nome:'Joao Silva',cliente_whatsapp:'5534999187189',cliente_email:'cliente@exemplo.com',plano:'mensal',dias:'30',valor:'149,90',computadores:'1',slots:'4',pedido_id:'pay_123456789',ambiente:'PRODUCAO',data_pagamento:'11/07/2026 14:30'};
const WA_EMOJIS=['✅','💰','⚠️','🚀','🔔','📦','💳','🛠️','📌','⏳','🎉','🙏','📲','🧾','🔐','⭐','🔥','👉','👇','☑️','❌','💬','📅','⏰'];
function getWaBody(){return document.getElementById('wa-body');}
async function loadNotificacoes(){const list=document.getElementById('wa-template-list');if(list)list.innerHTML='<div class="loading"><div class="spinner"></div> Carregando...</div>';const data=await apiFetch(API+'/whatsapp/templates');if(!data)return;S.waTemplates=data.templates||[];renderWhatsAppTemplateList();selectWhatsAppTemplate(S.waSelected||S.waTemplates[0]?.key);document.getElementById('lupdate').textContent='Atualizado: '+new Date().toLocaleTimeString('pt-BR');}
function renderWhatsAppTemplateList(){const list=document.getElementById('wa-template-list');if(!list)return;if(!S.waTemplates.length){list.innerHTML='<div class="empty">Nenhuma mensagem cadastrada.</div>';return;}list.innerHTML=S.waTemplates.map(t=>`<button class="template-option ${S.waSelected===t.key?'active':''}" onclick="selectWhatsAppTemplate('${esc(t.key)}')"><span class="template-option-title">${esc(t.title)}</span><span class="template-option-sub">${esc(t.audience)} | ${esc(t.usage||t.triggerDescription)}</span></button>`).join('');}
function selectWhatsAppTemplate(key){const t=(S.waTemplates||[]).find(x=>x.key===key);if(!t)return;S.waSelected=t.key;renderWhatsAppTemplateList();setText('wa-title',t.title);setText('wa-audience',t.audience);setText('wa-trigger',t.triggerDescription);setText('wa-updated',fmtDateTime(t.updatedAt));setText('wa-usage',t.usage||'');const keyEl=document.getElementById('wa-key');if(keyEl)keyEl.textContent=t.key;const body=getWaBody();if(body)body.value=t.body||'';const vars=document.getElementById('wa-vars');if(vars)vars.innerHTML=(t.variables||[]).map(v=>`<button class="var-chip" onclick="insertWhatsAppVariable('${esc(v)}')">{{${esc(v)}}}</button>`).join('');renderWhatsAppPreview(t.body||'');}
function waEscapeHtml(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');}
function renderWhatsAppMarkup(text){let out=waEscapeHtml(text);out=out.replace(/```([\s\S]+?)```/g,'<code>$1</code>');out=out.replace(/\*([^*\n][^*]*?)\*/g,'<strong>$1</strong>');out=out.replace(/_([^_\n][^_]*?)_/g,'<em>$1</em>');out=out.replace(/~([^~\n][^~]*?)~/g,'<s>$1</s>');return out;}
function renderWhatsAppPreview(body){let out=body||'';Object.keys(WA_SAMPLE).forEach(k=>{out=out.replaceAll('{{'+k+'}}',WA_SAMPLE[k]);});const preview=document.getElementById('wa-preview');if(preview)preview.innerHTML=renderWhatsAppMarkup(out);const count=document.getElementById('wa-count');if(count)count.textContent=(body||'').length+' caracteres';}
function insertAtCursor(text,wrapEnd){const body=getWaBody();if(!body)return;const start=body.selectionStart||0;const end=body.selectionEnd||0;const selected=body.value.slice(start,end);const finalText=wrapEnd!==undefined?text+(selected||'texto')+wrapEnd:text;body.value=body.value.slice(0,start)+finalText+body.value.slice(end);body.focus();const selectedLength=selected?selected.length:5;const cursor=wrapEnd!==undefined?start+text.length+selectedLength:start+finalText.length;body.selectionStart=body.selectionEnd=cursor;renderWhatsAppPreview(body.value);}
function insertWhatsAppVariable(name){insertAtCursor('{{'+name+'}}');}
function formatWhatsApp(kind){const map={bold:['*','*'],italic:['_','_'],strike:['~','~'],mono:['```','```']};const pair=map[kind];if(pair)insertAtCursor(pair[0],pair[1]);}
function toggleEmojiPanel(){const panel=document.getElementById('emoji-panel');if(!panel)return;if(!panel.innerHTML)panel.innerHTML=WA_EMOJIS.map(e=>`<button type="button" class="emoji-choice" data-emoji="${e}">${e}</button>`).join('');panel.classList.toggle('open');}
function insertEmoji(emoji){insertAtCursor(emoji);document.getElementById('emoji-panel')?.classList.remove('open');}
function wireWhatsAppEditor(){if(S.waEditorWired)return;S.waEditorWired=true;document.querySelectorAll('[data-wa-format]').forEach(btn=>{btn.addEventListener('click',()=>formatWhatsApp(btn.dataset.waFormat));});const emojiToggle=document.getElementById('wa-emoji-toggle');if(emojiToggle)emojiToggle.addEventListener('click',toggleEmojiPanel);const panel=document.getElementById('emoji-panel');if(panel)panel.addEventListener('click',e=>{const btn=e.target.closest('[data-emoji]');if(btn)insertEmoji(btn.dataset.emoji);});}
window.formatWhatsApp=formatWhatsApp;window.toggleEmojiPanel=toggleEmojiPanel;window.insertEmoji=insertEmoji;
async function saveWhatsAppTemplate(){if(!S.waSelected)return;const body=getWaBody()?.value||'';const r=await fetch(API+'/whatsapp/templates/'+encodeURIComponent(S.waSelected),{method:'PUT',headers:hdrs(),body:JSON.stringify({Body:body})});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao salvar mensagem.');return;}const idx=S.waTemplates.findIndex(x=>x.key===S.waSelected);if(idx>=0)S.waTemplates[idx]=data.template;showAdminMessage('success',data.msg||'Mensagem atualizada.');selectWhatsAppTemplate(S.waSelected);}
async function resetWhatsAppTemplate(){if(!S.waSelected)return;if(!await askAdminConfirm('Restaurar esta mensagem para o texto padrao?'))return;const r=await fetch(API+'/whatsapp/templates/'+encodeURIComponent(S.waSelected)+'/reset',{method:'POST',headers:hdrs()});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao restaurar mensagem.');return;}const idx=S.waTemplates.findIndex(x=>x.key===S.waSelected);if(idx>=0)S.waTemplates[idx]=data.template;showAdminMessage('success',data.msg||'Mensagem restaurada.');selectWhatsAppTemplate(S.waSelected);}
function openNewWhatsAppTemplate(){document.getElementById('wa-new-title').value='';document.getElementById('wa-new-audience').value='Personalizada';document.getElementById('wa-new-trigger').value='Mensagem personalizada criada no painel. Para envio automatico, vincule esta chave no backend.';document.getElementById('wa-new-body').value='';document.getElementById('modal-wa-template').classList.add('active');}
async function createWhatsAppTemplate(){const payload={Title:document.getElementById('wa-new-title').value.trim(),Audience:document.getElementById('wa-new-audience').value.trim(),TriggerDescription:document.getElementById('wa-new-trigger').value.trim(),Body:document.getElementById('wa-new-body').value};const r=await fetch(API+'/whatsapp/templates',{method:'POST',headers:hdrs(),body:JSON.stringify(payload)});const data=await r.json().catch(()=>null);if(!r.ok){showAdminMessage('error',data?.erro||'Falha ao criar mensagem.');return;}S.waTemplates.push(data.template);S.waSelected=data.template.key;closeModals();showAdminMessage('success',data.msg||'Mensagem criada.');renderWhatsAppTemplateList();selectWhatsAppTemplate(S.waSelected);}
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
  try{const r=await fetch('/admin/partials/modals.html',{cache:'no-store'});if(r.ok)target.innerHTML=await r.text();}
  catch(e){showAdminMessage('error','Nao foi possivel carregar os modais do admin.');}
}
loadAdminPartials().finally(init);
