# Regras de IA e Contexto do Repositorio (AI Rules)

**AVISO AOS AGENTES DE IA (AI ASSISTANTS):** A leitura deste arquivo e OBRIGATORIA antes de iniciar qualquer modificacao no projeto, sob instrucao direta do dono do repositorio.

## 1. Passo 0 - Contexto Geral
Sempre inicie lendo o arquivo README.md na raiz do projeto. Ele contem detalhes essenciais sobre a infraestrutura (LXC, Debian, Postgres local), fluxo de integracao (Asaas + Active Directory) e a finalidade do negocio (Venda de slots/computadores e liberacao automatica via AD).

## 2. Principios de Desenvolvimento
- O Frontend e Vanilla Javascript + HTML estatico servido na pasta wwwroot/. Nao invente frameworks complexos (React, Vue) nem introduza NPM.
- O painel do cliente (index.html e painel.html) usa TailwindCSS via CDN.
- O painel administrativo fica em `wwwroot/admin/` com telas HTML estaticas separadas, CSS Nativo/Puro compartilhado e Vanilla JavaScript compartilhado. Nao insira Tailwind nele sem permissao explicita. O `wwwroot/admin.html` existe apenas como entrada compativel/redirecionamento.
- A API e Stateless. Funcoes de sessao sao baseadas em JWT.
- O banco de dados relacional e orquestrado por Dapper (queries SQL puras), sem Entity Framework. Mantenha essa arquitetura de micro-ORM de alta performance. O arquivo DatabaseInitializer.cs e o responsavel por manter a estrutura do schema.
- As queries do Dapper devem usar explicitamente aliases (AS NomePropriedade) para fazer o mapeamento correto do snake_case do banco para o PascalCase do C#.
- O usuário da aplicação (premierhost_app) é o dono das tabelas. Alterações estruturais (DDL) feitas pelo DatabaseInitializer.cs rodam de forma nativa e automática na inicialização.
- O banco PostgreSQL deve ser criado em UTF8 para suportar emojis e Unicode. O encoding de um banco existente nao deve ser alterado via automacao: PostgreSQL exige dump, criacao de novo banco UTF8 e restore planejado. O DatabaseInitializer.cs pode diagnosticar/avisar, mas nunca deve recriar ou dropar o banco automaticamente.

## 3. Mapa de Arquivos e Responsabilidades (Directory Map)

### /Controllers (API Endpoints)
- AdminController.cs: Centro de comando administrativo. Gerencia usuarios (local e AD), cancelamentos de pedidos, listagem de estatisticas, cockpit financeiro/CRM e acoes coercitivas. O endpoint `GET /api/admin/dashboard` aceita periodos fixos e personalizados (`period`, `start`, `end`) e alimenta Dashboard, Financeiro e CRM do admin.
- AuthController.cs: Fluxo de autenticacao. Realiza o cadastro do usuario, criacao paralela da conta no Active Directory (tratando duplicacoes), Login via JWT e recuperacao de senha.
- CheckoutController.cs: Interface com o Asaas. Recalcula o valor do pedido no backend e gera um QR Code PIX estatico, individual, de valor fixo, pagamento unico e validade curta. Esse fluxo nao cadastra previamente o pagador nem solicita CPF/CNPJ; a conciliacao ocorre pelo `pixQrCodeId` recebido no webhook. Preserve no QR a descricao historica exata `Licença ({periodo}) - AnyDesk: {id}`, mas nao presuma que o Asaas a copiara para a cobranca criada automaticamente depois do pagamento.
- WebhookController.cs: Ponto vital. Recebe os gatilhos (webhooks) do Asaas. Pagamentos atuais por QR estatico devem ser identificados por `payment.pixQrCodeId` e conciliados com `orders.asaas_pix_qr_code_id`, nunca pela descricao automatica da cobranca. O teste `description.StartsWith("Licença")` existe somente para cobrancas dinamicas legadas. Quando um PIX e pago, este arquivo aprova o pedido, sincroniza nome/e-mail/WhatsApp do cadastro local no cliente Asaas, vincula-o ao grupo `PremierHost`, define `notificationDisabled=true`, desativa em lote todos os canais de notificacao do cliente e do provedor, e dispara a logica que concede acesso ao usuario no Active Directory.

### Regra critica de compatibilidade Asaas
- A escolha atual do produto e QR Pix estatico para nao solicitar CPF/CNPJ. Nao reverta para `/payments` dinamico sem autorizacao expressa: em producao o Asaas exige CPF/CNPJ do cliente nesse fluxo.
- O campo `description` enviado a `/pix/qrCodes/static` descreve o QR, mas a cobranca e criada automaticamente pelo Asaas somente quando o Pix e recebido e pode chegar aos webhooks com descricao automatica. Integracoes da mesma conta devem filtrar por `pixQrCodeId`, nao apenas pelo prefixo `Licença`.
- Nao tente atualizar a descricao apos `PAYMENT_RECEIVED`: cobrancas recebidas possuem restricoes de edicao e isso nao altera o payload que outros webhooks ja receberam.
- ProfileController.cs: Edicao de dados triviais do perfil do cliente.
- AnalyticsController.cs: Recebe somente eventos de produto presentes em uma lista permitida, remove propriedades nao autorizadas e salva telemetria first-party sem e-mail, WhatsApp, AnyDesk, codigo Pix ou senha. O schema e a retencao ficam no DatabaseInitializer.cs.

### /Services (Logica de Negocios e Background)
- ActiveDirectoryService.cs: O UNICO responsavel por conversar com o Windows Server (LDAP). Contem metodos para localizar, criar, deletar usuarios, definir senhas, editar (LdapModification) e mover eles entre OUs. Lembre-se: Suas buscas (GetUserDn) usam ScopeSub, logo varrem toda a estrutura partindo do BaseDn. Exige LDAPS (criptografia). Atente-se as limitacoes do LDAP no Linux: "O usuario nao pode alterar a senha" exige acesso ACL que e impossivel; e para renomear o Nome Completo (CN) e mandatorio utilizar ModifyDNRequest, e nao Modify regular.
- AdOrderExpirationWorker.cs: Job assincrono (Worker) que roda de 1 em 1 hora. Varre o banco de dados e expulsa/bloqueia do AD usuarios cujos planos expiraram e nao foram renovados.
- DatabaseInitializer.cs: Garante que o PostgreSQL tenha as tabelas formatadas corretamente. Se adicionar uma coluna nova, faca por aqui.
- TelegramLogger.cs: Servico global de monitoramento. Intercepta logs de erro (Nivel Error) e os encaminha diretamente para o Telegram do administrador.

### /wwwroot (Frontend)
- index.html: Landing page comercial, planos, teste gratuito, video demonstrativo, formulario de cadastro (validacao forte anti-fake), login e redefinicao.
- painel.html: SPA do usuario final e simulador publico de precos. Visitantes podem calcular sem login; login e exigido para gerar PIX, acessar indicacoes, perfil, historico e credenciais. Antes do PIX, o cliente informa o servidor de WYD; `wyd2` e `wyd 2` sao rejeitados exatamente pelo backend. O topo possui modais nativos de Como usar e Duvidas frequentes.
- vid/comofunciona.mp4: Video demonstrativo compartilhado pela landing e pelo modal Como usar. Referencie-o por URL da propria origem (`/vid/comofunciona.mp4`). Ao adicionar midia externa, revise explicitamente a diretiva `media-src` da CSP em Program.cs.
- admin.html: Entrada compativel que redireciona para `/admin/dashboard.html`.
- admin/: Painel Administrativo dividido em telas HTML estaticas (`dashboard.html`, `financeiro.html`, `crm.html`, `pedidos.html`, `usuarios.html`, `active-directory.html`), com UI customizada, modais nativos, fetch direto e CSS/JS compartilhados em `admin/assets/`. Mantenha CSS nativo/puro e reaproveite o endpoint `/api/admin/dashboard` para metricas de receita, pedidos, clientes, vencimentos e fila operacional.
