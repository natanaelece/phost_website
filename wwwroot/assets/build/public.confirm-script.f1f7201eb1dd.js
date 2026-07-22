window.addEventListener('DOMContentLoaded', async () => {
            const urlParams = new URLSearchParams(window.location.search);
            const token = urlParams.get('token');
            const title = document.getElementById('statusTitle');
            const msg = document.getElementById('statusMsg');
            const loader = document.getElementById('loader');
            const btn = document.getElementById('btnLogin');

			if (token) {
				window.history.replaceState({}, document.title, window.location.pathname);
			}

            if (!token) {
                title.innerText = "Erro de validação";
                title.classList.replace('text-white', 'text-red-500');
                msg.innerText = "O token de confirmação não foi encontrado na URL.";
                loader.classList.add('hidden');
                btn.classList.remove('hidden');
                return;
            }

            try {
                // Chama a API para validar o token no banco de dados
                const res = await fetch(`/api/auth/confirm-email?token=${token}`);
                const data = await res.json();

                loader.classList.add('hidden');

                if (res.ok) {
                    title.innerText = "E-mail confirmado!";
                    title.classList.replace('text-white', 'text-green-500');
                    msg.innerText = data.mensagem || "Sua conta foi ativada com sucesso. Você já pode fazer login no painel.";

					if (data.email) {
						btn.href = `/?action=login&email=${encodeURIComponent(data.email)}`;
					} else {
						btn.href = "/?action=login";
					}

                    btn.classList.remove('hidden');
                    window.premierAnalytics?.track('email_confirmed', { result: 'success' });
                } else {
                    title.innerText = "Falha na Ativação";
                    title.classList.replace('text-white', 'text-red-500');
                    msg.innerText = data.erro || "Este token é inválido ou já expirou.";
                    btn.classList.remove('hidden');
                }
            } catch (err) {
                loader.classList.add('hidden');
                title.innerText = "Erro de Conexão";
                title.classList.replace('text-white', 'text-red-500');
                msg.innerText = "Não foi possível se comunicar com o servidor da API. Tente novamente mais tarde.";
                btn.classList.remove('hidden');
            }
        });
