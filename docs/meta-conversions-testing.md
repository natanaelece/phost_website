# Validação do Meta Pixel e Conversions API

## Resultado final — 24 de julho de 2026

A rodada operacional começou às `09:40:23 -03:00` em uma sessão anônima limpa.
Antes da escolha, não houve carregamento de `connect.facebook.net`, chamada a
`www.facebook.com/tr` nem chamada ao endpoint first-party de eventos.

Depois da recusa inicial, a navegação por `/`, `/guia-wyd`, `/painel` e
`/privacidade`, seguida de um clique controlado em WhatsApp sem navegação
externa, permaneceu sem Pixel, evento automático ou CAPI. `_fbp` e `_fbc` não
foram criados.

Após o aceite:

- o `fbevents.js` foi carregado uma vez por página;
- `PageView` foi enviado como evento padrão (`ev=PageView`) uma vez por
  carregamento;
- `ViewContent` foi aceito no navegador e no endpoint servidor com o mesmo
  `event_id`: `ad1083a3-3350-45e4-aaea-b5bcdbc2280a`;
- `Contact` foi aceito no navegador e no endpoint servidor com o mesmo
  `event_id`: `7af87403-9167-48fa-816b-ef1726fa08f6`;
- eventos automáticos habilitados pela própria biblioteca da Meta foram
  permitidos somente depois do aceite.

Na revogação, a preferência `rejected` foi persistida, os cookies `_fbp` e
`_fbc` acessíveis nos escopos host e domínio foram removidos e a página foi
recarregada. Depois do reload, as quatro páginas e o clique controlado em
WhatsApp permaneceram por mais de 30 segundos sem novo Pixel, CAPI ou evento
automático.

O Chromium oficial do projeto passou nas 16 páginas, com zero violações CSP,
zero exceções JavaScript e zero falhas de interação. No site público, a
Cloudflare tentou injetar `static.cloudflareinsights.com/beacon.min.js`; a CSP
estrita bloqueou esse beacon, que não foi adicionado às origens permitidas.

## Eventos de servidor sintéticos

A ferramenta manual `--meta-capi-smoke` utilizou o mesmo
`MetaConversionsService`, com `test_event_code=TEST30146`, dados fictícios e
sem acessar banco comercial, Asaas, AD ou mensageria. A Graph API aceitou:

- `CompleteRegistration`:
  `smoke_complete_registration_20260724123550294b1cf8adabc8c4f`;
- `Lead`: `smoke_lead_20260724123550294b1cf8adabc8c4f`;
- `StartTrial`: `smoke_start_trial_20260724123550294b1cf8adabc8c4f`;
- `InitiateCheckout`:
  `smoke_initiate_checkout_20260724123550294b1cf8adabc8c4f`;
- `Purchase`: `smoke_purchase_20260724123550294b1cf8adabc8c4f`.

A segunda tentativa do mesmo `Purchase` retornou `Duplicate` e não realizou
outro envio HTTP.

Os testes automatizados com `HttpMessageHandler` simulado validaram os momentos
de negócio, normalização, SHA-256, omissão de campos vazios, campos técnicos
sem hash, ausência de `_fbp`/`_fbc` quando indisponíveis, consentimento,
timeout, falha HTTP, IDs determinísticos e idempotência de `Purchase`.

## Retirada do modo de teste

Depois de confirmar os eventos novos em **Meta → PremierHost → Eventos de
teste → Site**:

1. faça backup protegido do arquivo `/etc/premierapi/premierapi.env`;
2. remova somente a linha `META_CAPI_TEST_EVENT_CODE`;
3. preserve `META_DATASET_ID`, `META_CAPI_ACCESS_TOKEN` e
   `META_GRAPH_API_VERSION`;
4. mantenha o arquivo pertencente a `root`, modo `0600`;
5. execute o procedimento oficial `./restart-completo.sh`;
6. confirme `active/running`, health HTTP 200 e ausência de crescimento
   anormal em `NRestarts`;
7. valide que a configuração carregada não possui mais
   `test_event_code`, sem imprimir valores de configuração.

Não gere outro token, não habilite o canal Offline e mantenha a origem como
Site.
