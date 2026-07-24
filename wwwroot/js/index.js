let authModalTrigger = null;
        let registerStep = 0;
        const registerStepCount = 6;
        let validatedReferralCode = null;
        function fecharMenuMobile() {
            document.getElementById('mobileMenuIndex').classList.add('hidden');
        }

        function solicitarTesteGratis() {
            sessionStorage.setItem('premier_post_auth_intent', 'free-trial');
            window.premierAnalytics?.track('free_trial_cta_clicked', { source: 'landing' });
            const token = localStorage.getItem('premier_token');
            const user = localStorage.getItem('premier_user');
            if (token && user) {
                window.location.href = '/painel?intent=free-trial#teste-gratis';
                return;
            }
            abrirModalAuth('login');
        }

        async function applyFreeTrialEligibility() {
            const token = localStorage.getItem('premier_token');
            if (!token) return;
            try {
                const response = await fetch('/api/free-trial/me', { headers: { 'X-Session-Token': token } });
                if (!response.ok) return;
                const status = await response.json();
                if (!status?.hasPaidOrder) return;
                document.getElementById('teste')?.classList.add('hidden');
                document.querySelectorAll('[data-free-trial-cta]').forEach(element => element.classList.add('hidden'));
            } catch {}
        }

        function abrirModalAuth(tab = 'login') {
            const token = localStorage.getItem('premier_token');
            const user = localStorage.getItem('premier_user');

            if (token && user) {
                const target = sessionStorage.getItem('premier_post_auth_intent') === 'free-trial'
                    ? '/painel?intent=free-trial#teste-gratis'
                    : '/painel';
                window.location.href = target;
            } else {
                // Limpa resíduos de sessões corrompidas/expiradas que causam o loop
                localStorage.removeItem('premier_token');
                localStorage.removeItem('premier_user');

                authModalTrigger = document.activeElement;
                switchTab(tab);
                document.getElementById('authModal').classList.remove('hidden');
                document.body.classList.add('overflow-hidden');
                window.premierAnalytics?.track('auth_opened', { source: tab });
                requestAnimationFrame(() => document.querySelector(`#${tab === 'register' ? 'registerForm' : tab === 'recover' ? 'recoverForm' : 'loginForm'} input`)?.focus());
            }
        }

        function fecharModalAuth() {
            document.getElementById('authModal').classList.add('hidden');
            document.body.classList.remove('overflow-hidden');
            authModalTrigger?.focus();
        }
        function mascaraTelefone(input) { let v = input.value.replace(/\D/g, ''); if(v.length>11) v=v.slice(0,11); if(v.length>2) v=`(${v.slice(0,2)}) ${v.slice(2)}`; if(v.length>10) v=`${v.slice(0,10)}-${v.slice(10)}`; input.value=v; }
        function markInvalid(input) { input.classList.add('border-red-500'); input.setAttribute('aria-invalid','true'); }
        function limparErro(inputId, msgId) { const input=document.getElementById(inputId); input.classList.remove('border-red-500'); input.setAttribute('aria-invalid','false'); document.getElementById(msgId).classList.add('hidden'); document.getElementById('regErrorMsg').classList.add('hidden'); document.getElementById('loginErrorMsg').classList.add('hidden'); document.getElementById('recoverErrorMsg').classList.add('hidden'); }
        function handleReferralInput(){validatedReferralCode=null;limparErro('regRef','refErrorMsg');}

        function setRegisterStep(step, focus = true) {
            registerStep=Math.max(0,Math.min(registerStepCount-1,step));
            document.querySelectorAll('[data-reg-step]').forEach(el=>el.classList.toggle('hidden',Number(el.dataset.regStep)!==registerStep));
            document.getElementById('regStepLabel').innerText=`Etapa ${registerStep+1} de ${registerStepCount}`;
            document.getElementById('regProgressBar').style.width=`${((registerStep+1)/registerStepCount)*100}%`;
            const progress=document.querySelector('#registerForm [role="progressbar"]');progress?.setAttribute('aria-valuenow',String(registerStep+1));
            document.getElementById('btnRegBack').classList.toggle('hidden',registerStep===0);
            document.getElementById('btnRegNext').classList.toggle('hidden',registerStep===registerStepCount-1);
            document.getElementById('btnRegSubmit').classList.toggle('hidden',registerStep!==registerStepCount-1);
            if(focus)requestAnimationFrame(()=>document.querySelector(`[data-reg-step="${registerStep}"] input`)?.focus());
        }

        function validateRegisterStep(step) {
            const name=document.getElementById('regName'),wa=document.getElementById('regWhatsapp'),email=document.getElementById('regEmail'),pass=document.getElementById('regPass'),privacy=document.getElementById('regPrivacy');
            if(step===0&&name.value.trim().split(/\s+/).length<2){markInvalid(name);document.getElementById('nameErrorMsg').classList.remove('hidden');return false;}
            if(step===1&&wa.value.replace(/\D/g,'').length<10){markInvalid(wa);document.getElementById('waErrorMsg').classList.remove('hidden');return false;}
            if(step===2&&!email.checkValidity()){markInvalid(email);document.getElementById('emailErrorMsg').classList.remove('hidden');return false;}
            if(step===3&&(pass.value.length<6||pass.value.length>12)){markInvalid(pass);document.getElementById('passErrorMsg').classList.remove('hidden');return false;}
            if(step===5&&!privacy.checked){document.getElementById('privacyErrorMsg').classList.remove('hidden');return false;}
            return true;
        }

        async function validateReferralStep(){
            const input=document.getElementById('regRef'),error=document.getElementById('refErrorMsg');
            const code=input.value.trim().toUpperCase();
            input.value=code;
            if(!code){validatedReferralCode='';return true;}
            if(validatedReferralCode===code)return true;

            const button=document.getElementById('btnRegNext'),originalText=button.innerText;
            button.disabled=true;button.innerText='Validando...';
            try{
                const response=await fetch('/api/auth/validate-referral',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
                if(!response.ok)throw new Error('Não foi possível validar o código agora.');
                const data=await response.json();
                if(!data.valid){markInvalid(input);error.innerText='Código de indicação inválido.';error.classList.remove('hidden');return false;}
                validatedReferralCode=code;
                return true;
            }catch(e){
                markInvalid(input);error.innerText=e.message||'Não foi possível validar o código agora.';error.classList.remove('hidden');return false;
            }finally{button.disabled=false;button.innerText=originalText;}
        }

        async function nextRegisterStep(){
            if(!validateRegisterStep(registerStep))return;
            if(registerStep===4&&!await validateReferralStep())return;
            setRegisterStep(registerStep+1);
        }
        function previousRegisterStep(){setRegisterStep(registerStep-1);}
        function showRegisterSuccess(email){document.getElementById('authModal').classList.add('hidden');document.getElementById('registerSuccessModal').classList.remove('hidden');document.getElementById('logEmail').value=email;requestAnimationFrame(()=>document.querySelector('#registerSuccessModal button')?.focus());}
        function closeRegisterSuccess(){document.getElementById('registerSuccessModal').classList.add('hidden');document.body.classList.remove('overflow-hidden');authModalTrigger?.focus();}

        function switchTab(tab) {
            const lForm = document.getElementById('loginForm'), rForm = document.getElementById('registerForm'), recForm = document.getElementById('recoverForm');
            const tLog = document.getElementById('tabLogin'), tReg = document.getElementById('tabRegister');
            const tabsContainer = document.getElementById('tabsContainer');
            tLog.setAttribute('aria-selected', String(tab === 'login'));
            tReg.setAttribute('aria-selected', String(tab === 'register'));

            lForm.classList.add('hidden');
            rForm.classList.add('hidden');
            recForm.classList.add('hidden');

            if (tab === 'login') {
                lForm.classList.remove('hidden');
                tabsContainer.classList.remove('hidden');
                tLog.className = "flex-1 py-3 text-sm font-bold text-blue-400 border-b-2 border-blue-500 transition-all";
                tReg.className = "flex-1 py-3 text-sm font-bold text-slate-500 border-b-2 border-transparent hover:text-slate-400 transition-all";
            } else if (tab === 'register') {
                window.premierAnalytics?.track('signup_started', { source: 'auth_modal' });
                rForm.classList.remove('hidden');
                setRegisterStep(0, false);
                tabsContainer.classList.remove('hidden');
                tReg.className = "flex-1 py-3 text-sm font-bold text-blue-400 border-b-2 border-blue-500 transition-all";
                tLog.className = "flex-1 py-3 text-sm font-bold text-slate-500 border-b-2 border-transparent hover:text-slate-400 transition-all";
            } else if (tab === 'recover') {
                recForm.classList.remove('hidden');
                tabsContainer.classList.add('hidden');
            }
        }

        document.addEventListener('keydown', event => {
            const modal = document.getElementById('authModal');
            const successModal=document.getElementById('registerSuccessModal');
            if(!successModal.classList.contains('hidden')&&event.key==='Tab'){event.preventDefault();successModal.querySelector('button')?.focus();return;}
            if (event.key === 'Escape' && !modal.classList.contains('hidden')) fecharModalAuth();
            if (event.key === 'Tab' && !modal.classList.contains('hidden')) {
                const focusable = [...modal.querySelectorAll('button:not([disabled]),a[href],input:not([disabled])')].filter(el => !el.closest('.hidden'));
                if (!focusable.length) return;
                const first = focusable[0], last = focusable[focusable.length - 1];
                if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
                else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
            }
        });

        document.getElementById('loginForm').addEventListener('submit', async (e) => {
            e.preventDefault();

            let hasError = false;
            const emailInput = document.getElementById('logEmail');
            const passInput = document.getElementById('logPass');

            if (!emailInput.value.trim() || !emailInput.value.includes('@')) {
                markInvalid(emailInput);
                document.getElementById('logEmailError').classList.remove('hidden');
                hasError = true;
            }
            if (!passInput.value.trim()) {
                markInvalid(passInput);
                document.getElementById('logPassError').classList.remove('hidden');
                hasError = true;
            }

            if (hasError) return;

            const btn = document.getElementById('btnLoginSubmit'), err = document.getElementById('loginErrorMsg');
            btn.innerHTML = 'Aguarde...'; btn.disabled = true; err.classList.add('hidden');

            try {
                // Captura a resposta do Cloudflare Turnstile
                const cfResponse = document.querySelector('#loginForm [name="cf-turnstile-response"]')?.value;

                const res = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: window.premierMeta?.withAttributionHeaders({ 'Content-Type': 'application/json' })
                        || { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        Email: emailInput.value,
                        Password: passInput.value,
                        "cf-turnstile-response": cfResponse // O C# deve validar isso
                    })
                });
                const data = await res.json();

                // Trata a flag de e-mail não confirmado conforme exigido na arquitetura
                if(!res.ok) {
                    if(data.erro && data.erro.includes("não confirmado")) {
                        throw new Error("Por favor, verifique sua caixa de entrada e confirme seu e-mail antes de fazer login.");
                    }
                    throw new Error(data.erro || "Erro de servidor");
                }

                localStorage.setItem('premier_token', data.token);
                localStorage.setItem('premier_user', JSON.stringify(data.user));
                if (data.isAdmin) {
                    localStorage.setItem('premier_isAdmin', 'true');
                } else {
                    localStorage.removeItem('premier_isAdmin');
                }
                window.location.href = '/painel';
            } catch(e) {
                err.innerText = e.message; err.classList.remove('hidden');
            } finally { btn.innerHTML = 'Entrar no Painel'; btn.disabled = false; }
        });

        document.getElementById('registerForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            if(registerStep<registerStepCount-1){await nextRegisterStep();return;}
            const nameInput = document.getElementById('regName');
            const waInput = document.getElementById('regWhatsapp');
            const emailInput = document.getElementById('regEmail');
            const passInput = document.getElementById('regPass');
            const privacyCheck = document.getElementById('regPrivacy');

            const invalidStep=[0,1,2,3,4,5].find(step=>!validateRegisterStep(step));
            if(invalidStep!==undefined){setRegisterStep(invalidStep);return;}
            if(!await validateReferralStep()){setRegisterStep(4);return;}

            const btn = document.getElementById('btnRegSubmit'), err = document.getElementById('regErrorMsg');
            btn.innerHTML = 'Aguarde...'; btn.disabled = true; err.classList.add('hidden');

            try {
                const cfResponse = document.querySelector('#registerForm [name="cf-turnstile-response"]')?.value;

                const res = await fetch('/api/auth/register', {
                    method: 'POST',
                    headers: window.premierMeta?.withAttributionHeaders({ 'Content-Type': 'application/json' })
                        || { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        Name: nameInput.value.trim(),
                        Email: emailInput.value.trim(),
                        Whatsapp: waInput.value.trim(),
                        Password: passInput.value,
                        ReferralCode: document.getElementById('regRef').value.trim(),
                        "cf-turnstile-response": cfResponse
                    })
                });
                const data = await res.json();

                if(!res.ok) {
                    if (data.erro && data.erro.includes("indicação")) {
                        document.getElementById('regRef').classList.add('border-red-500');
                        document.getElementById('refErrorMsg').innerText = data.erro;
                        document.getElementById('refErrorMsg').classList.remove('hidden');
                        setRegisterStep(4);
                    } else {
                        err.innerText = data.erro || "Erro ao registrar";
                        err.classList.remove('hidden');
                    }
                    throw new Error(data.erro || "Erro ao registrar");
                }

                window.premierAnalytics?.track('signup_completed', { result: 'success' });
                showRegisterSuccess(emailInput.value.trim());
            } catch(e) {
                if(document.getElementById('refErrorMsg').classList.contains('hidden')&&err.classList.contains('hidden')){err.innerText=e.message||'Não foi possível concluir o cadastro.';err.classList.remove('hidden');}
            } finally { btn.innerHTML = 'Criar minha conta'; btn.disabled = false; }
        });

		async function handleRecover(event) {
			event.preventDefault();
			const email = document.getElementById('recEmail').value.trim();
			const errorMsg = document.getElementById('recoverErrorMsg');
			const successMsg = document.getElementById('toastRecoverSuccess');
			const btn = document.getElementById('btnRecoverSubmit');

			// Validação
			if (!email) {
				document.getElementById('recEmail').classList.add('border-red-500');
				document.getElementById('recEmailError').classList.remove('hidden');
				return false;
			}

			// Pega o token do Turnstile
			//const turnstileResponse = document.querySelector('#recoverForm .cf-turnstile iframe');
			let turnstileToken = '';
			if (window.turnstile) {
				turnstileToken = window.turnstile.getResponse();
			}

			errorMsg.classList.add('hidden');
			successMsg.classList.add('hidden');
			btn.disabled = true;
			btn.innerText = 'Enviando...';

			try {
				const res = await fetch('/api/auth/forgot-password', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({
						email: email,
						'cf-turnstile-response': turnstileToken
					})
				});
				const data = await res.json();

				if (res.ok) {
					successMsg.classList.remove('hidden');
					document.getElementById('recEmail').value = '';
					if (window.turnstile) { document.querySelectorAll(".cf-turnstile").forEach(el => turnstile.reset(el)); }
				} else {
					errorMsg.innerText = data.erro || 'Erro ao enviar link. Tente novamente.';
					errorMsg.classList.remove('hidden');
					if (window.turnstile) { document.querySelectorAll(".cf-turnstile").forEach(el => turnstile.reset(el)); }
				}
			} catch (err) {
				errorMsg.innerText = 'Erro de conexão. Tente novamente.';
				errorMsg.classList.remove('hidden');
			} finally {
				btn.disabled = false;
				btn.innerText = 'Enviar Link';
			}
			return false;
		}

			document.addEventListener("DOMContentLoaded", function() {
			applyFreeTrialEligibility();
			const urlParams = new URLSearchParams(window.location.search);
			if (urlParams.get('intent') === 'free-trial') {
				sessionStorage.setItem('premier_post_auth_intent', 'free-trial');
			}

			const action = urlParams.get('action');

			if (action === 'login' || action === 'register') {
				const emailParam = urlParams.get('email');

				// Abre o modal primeiro para garantir que o input exista na tela
				if (typeof abrirModalAuth === "function") {
					abrirModalAuth(action);
				}

				// Preenche o campo usando o ID correto 'logEmail'
				if (emailParam) {
					const emailInput = document.getElementById('logEmail');
					if (emailInput) {
						emailInput.value = emailParam;
					}
				}

				// Limpa a URL por último para não atrapalhar a leitura dos dados
				window.history.replaceState({}, document.title, "/");
			}
		});
