function getTokenFromUrl() {
            const queryParams = new URLSearchParams(window.location.search);
            const queryToken = queryParams.get('token');

            if (queryToken) {
                return queryToken;
            }

            if (window.location.hash) {
                const hash = window.location.hash.replace(/^#/, '');
                const hashParams = new URLSearchParams(hash);
                const hashToken = hashParams.get('token');

                if (hashToken) {
                    return hashToken;
                }

                if (hash.startsWith('token=')) {
                    return hash.replace('token=', '').trim();
                }
            }

            return '';
        }

        function limparUrlSensivel() {
            window.history.replaceState({}, document.title, '/recuperar-senha');
        }

        function mostrarErro(msg) {
            document.getElementById('loadingBox').classList.add('hidden');
            document.getElementById('resetForm').classList.add('hidden');

            const errorMsg = document.getElementById('errorMsg');
            errorMsg.innerText = msg;
            errorMsg.classList.remove('hidden');

            document.getElementById('pageSubtitle').innerText = 'Não foi possível validar seu link.';
            document.getElementById('btnLogin').classList.remove('hidden');
        }

        function mostrarFormulario() {
            document.getElementById('loadingBox').classList.add('hidden');
            document.getElementById('errorMsg').classList.add('hidden');
            document.getElementById('successMsg').classList.add('hidden');
            document.getElementById('btnLogin').classList.add('hidden');
            document.getElementById('resetForm').classList.remove('hidden');
            document.getElementById('pageSubtitle').innerText = 'Digite sua nova senha.';
        }

		function limparErroReset(inputId, msgId) {
			document.getElementById(inputId).classList.remove('input-error');
			document.getElementById(msgId).classList.add('hidden');
			document.getElementById('errorMsg').classList.add('hidden');
		}

		function mostrarErroCampo(inputId, msgId, msg) {
			const input = document.getElementById(inputId);
			const msgElement = document.getElementById(msgId);

			input.classList.add('input-error');
			msgElement.innerText = msg;
			msgElement.classList.remove('hidden');
		}

        async function validarTokenInicial() {
            const tokenUrl = getTokenFromUrl();

            if (tokenUrl) {
                sessionStorage.setItem('premier_reset_token', tokenUrl);
            }

            limparUrlSensivel();

            const token = sessionStorage.getItem('premier_reset_token');

            if (!token) {
                mostrarErro('Token de recuperação não encontrado. Solicite um novo link.');
                return;
            }

            try {
                const res = await fetch('/api/auth/validate-reset-token?token=' + encodeURIComponent(token), {
                    method: 'GET',
                    headers: { 'Accept': 'application/json' }
                });

                const data = await res.json().catch(() => ({}));

                if (!res.ok || !data.valid) {
                    sessionStorage.removeItem('premier_reset_token');
                    mostrarErro(data.erro || 'Token inválido ou expirado. Solicite um novo link de recuperação.');
                    return;
                }

                mostrarFormulario();
            } catch (err) {
                mostrarErro('Erro ao validar o link. Tente novamente em alguns instantes.');
            }
        }

        async function handleReset(event) {
            event.preventDefault();

            const token = sessionStorage.getItem('premier_reset_token');
            const newPass = document.getElementById('newPass').value;
            const confirmPass = document.getElementById('confirmPass').value;
            const errorMsg = document.getElementById('errorMsg');
            const successMsg = document.getElementById('successMsg');
            const btn = document.getElementById('btnReset');
            const btnLogin = document.getElementById('btnLogin');

            errorMsg.classList.add('hidden');
            successMsg.classList.add('hidden');
            limparErroReset('newPass', 'newPassError');
            limparErroReset('confirmPass', 'confirmPassError');

            if (!token) {
                mostrarErro('Token de recuperação não encontrado. Solicite um novo link.');
                return false;
            }

            if (!newPass.trim()) {
                mostrarErroCampo('newPass', 'newPassError', 'Por favor, informe sua nova senha.');
                return false;
            }

            if (!confirmPass.trim()) {
                mostrarErroCampo('confirmPass', 'confirmPassError', 'Por favor, confirme sua nova senha.');
                return false;
            }

            if (newPass.length < 6 || newPass.length > 12) {
                mostrarErroCampo('newPass', 'newPassError', 'A senha deve ter entre 6 e 12 caracteres.');
                return false;
            }

            if (newPass !== confirmPass) {
                mostrarErroCampo('confirmPass', 'confirmPassError', 'As senhas não coincidem.');
                return false;
            }

            btn.disabled = true;
            btn.innerText = 'Redefinindo...';

            try {
                const res = await fetch('/api/auth/reset-password', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        token: token,
                        newPassword: newPass
                    })
                });

                const data = await res.json().catch(() => ({}));

                if (res.ok) {
                    sessionStorage.removeItem('premier_reset_token');

                    const loginUrl = data.email
                        ? `/?action=login&email=${encodeURIComponent(data.email)}`
                        : '/?action=login';

                    successMsg.innerText = data.mensagem || 'Senha alterada com sucesso! Faça login com sua nova senha.';
                    successMsg.classList.remove('hidden');

                    document.getElementById('resetForm').classList.add('hidden');
                    document.getElementById('pageSubtitle').innerText = 'Senha redefinida com sucesso.';

                    btnLogin.href = loginUrl;
                    btnLogin.classList.remove('hidden');

                    setTimeout(() => {
                        window.location.href = loginUrl;
                    }, 2500);
                } else {
                    errorMsg.innerText = data.erro || 'Erro ao redefinir senha.';
                    errorMsg.classList.remove('hidden');
                }
            } catch (err) {
                errorMsg.innerText = 'Erro de conexão. Tente novamente.';
                errorMsg.classList.remove('hidden');
            } finally {
                btn.disabled = false;
                btn.innerText = 'Redefinir Senha';
            }

            return false;
        }

        document.addEventListener('DOMContentLoaded', validarTokenInicial);
