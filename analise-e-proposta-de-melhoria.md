# Análise e proposta de melhoria de UX, UI e Product Analytics

## Escopo da análise

Foram lidos integralmente os arquivos `README.md`, `rules.md`, `.cursorrules` e `.agents/AGENTS.md` antes da avaliação.

A análise foi realizada sobre o código e os fluxos existentes. O servidor local não estava disponível nas portas 5000/5001 no momento da inspeção; portanto, a avaliação visual foi estática, sem teste renderizado ou sessão autenticada.

Nenhum arquivo funcional do projeto foi alterado durante a auditoria.

## Diagnóstico geral

| Área | Maturidade | Avaliação |
|---|---:|---|
| UX Writing | 6/10 | Há textos claros e contextuais, mas também inconsistências, jargão e CTAs que não representam corretamente a próxima ação |
| Heurísticas de Nielsen | 6/10 | O produto oferece feedback, prevenção de erros e ajuda; acessibilidade, consistência e recuperação ainda precisam evoluir |
| UI pública/cliente | 7/10 | Hierarquia visual e identidade coerentes, simulador bem organizado e responsividade planejada |
| UI administrativa | 5/10 | Boa densidade operacional, mas mobile, acessibilidade e excesso de ações por tabela são problemas relevantes |
| Product Analytics | 2/10 | Existem métricas financeiras, mas não há instrumentação comportamental do funil |

## O que o projeto já faz bem

### UX e UX Writing

O sistema explica razoavelmente bem a proposta e reduz dúvidas antes da compra:

- Landing com benefícios, vídeo, teste grátis, planos e explicação de segurança.
- Simulador disponível sem autenticação.
- FAQ e “Como usar” no painel.
- Resumo do pedido com descontos discriminados e total destacado.
- Mensagens específicas para campos inválidos.
- Estados como pedido pendente, PIX expirando, carregamento, sucesso e falha.
- Política de privacidade integrada ao cadastro.

A decisão de permitir simulação antes do login é particularmente boa: reduz a barreira inicial e respeita a intenção de consultar antes de cadastrar.

### Heurísticas de Nielsen presentes

Há aplicação parcial e concreta de várias heurísticas:

- **Visibilidade do estado:** spinners, “Salvando…”, contador do PIX, pedido pendente e mensagens de sucesso.
- **Correspondência com o mundo real:** valores em reais, planos por período, quantidade de computadores e instâncias.
- **Controle do usuário:** cancelar ou retomar PIX, remover cupom, fechar modais e cancelar ações administrativas.
- **Prevenção de erros:** validação local, confirmação de ações destrutivas e recálculo do preço no backend.
- **Reconhecimento em vez de memorização:** resumo visível, FAQ, cards explicativos e dados do pedido.
- **Ajuda e documentação:** vídeo, tutorial em quatro passos e dúvidas frequentes.

O painel administrativo também já apresenta métricas de receita, ticket médio, clientes, vencimentos e fila operacional. Porém, a “conversão” atual é apenas `pedidos pagos / pedidos criados`, e não a conversão completa desde a visita ou simulação (`Controllers/AdminController.cs`, próximo à linha 339).

## Principais problemas encontrados

### 1. O CTA principal cria uma barreira desnecessária

Na landing, “Ver planos e contratar” abre diretamente o cadastro (`wwwroot/index.html`, próximo à linha 125), embora o simulador seja público.

Isso cria uma inconsistência entre promessa e ação: o visitante pensa que verá preços, mas recebe um formulário.

#### Ajuste recomendado

- CTA principal: **“Simular preço”** → `/painel#simular-planos`.
- CTA secundário: **“Entrar na minha conta”**.
- Pedir autenticação somente quando o usuário clicar em gerar o PIX.

**Vantagem:** menor atrito e provável aumento da chegada ao simulador.

**Desvantagem:** mais visitantes poderão simular sem se cadastrar; será necessário medir se isso melhora compras reais.

### 2. A linguagem inicial do painel não corresponde à tarefa

O painel começa com “Configuração da Licença” e “Monte sua topologia e obtenha o código de acesso”. “Topologia” é linguagem técnica e o acesso não é obtido imediatamente.

#### Texto sugerido

> **Monte seu plano e veja o preço**
> Escolha computadores, instâncias e período. Você só precisa entrar para gerar o PIX.

Outros ajustes sugeridos:

- “Qt. Computadores” → “Quantidade de computadores”.
- “Instâncias por Computador” → “Acessos por computador”, caso “instância” não seja um termo já dominado pelos clientes.
- “Seu ID AnyDesk” → “ID do AnyDesk para configuração”.
- “WhatsApp Para Instalação” → “WhatsApp para receber as instruções”.
- “RETOMAR PIX” → “Continuar pagamento”.
- “Registrar” → “Criar minha conta”.
- “Enviar Link” → “Enviar link de recuperação”.

**Vantagem:** reduz esforço cognitivo e aproxima o texto da intenção do usuário.

**Desvantagem:** trocar “instância” exige confirmar com clientes se o termo atual já faz parte do vocabulário da comunidade.

### 3. Acessibilidade dos formulários

Vários elementos `label` não possuem atributo `for`, mesmo quando o campo tem `id`, por exemplo no login e cadastro (`wwwroot/index.html`, próximo à linha 183). Isso prejudica leitores de tela e reduz a área clicável do rótulo.

Também foram identificados os seguintes pontos:

- O modal de autenticação não possui `role="dialog"`, `aria-modal` ou título associado.
- Botões de fechar compostos apenas por SVG não possuem nome acessível.
- Mensagens de erro e sucesso geralmente não usam `role="alert"` ou `aria-live`.
- Há uso frequente de `outline-none`, sem um substituto consistente de foco visível.
- Tooltips dependem de `hover`, dificultando o acesso por teclado e celular.
- O botão do histórico remove explicitamente seu contorno de foco.
- Modais não mantêm o foco dentro deles nem devolvem o foco ao elemento que os abriu.
- Os modais administrativos são apenas `div`, sem semântica de diálogo.
- Labels administrativos também não estão associados aos campos.

#### Ajustes prioritários

- Associar todos os rótulos com `for`.
- Adicionar `aria-describedby` ligando campos às mensagens de erro.
- Adicionar `aria-invalid` durante falhas.
- Transformar feedback em regiões `aria-live`.
- Implementar gerenciamento de foco e fechamento com `Escape` em todos os modais.
- Criar estilo global de `:focus-visible`.
- Adicionar `aria-label` aos botões somente com ícone.
- Respeitar `prefers-reduced-motion`.

**Vantagem:** melhora teclado, leitores de tela, clareza visual e conformidade.

**Desvantagem:** exige revisão transversal dos HTMLs e dos componentes gerados por JavaScript.

### 4. Responsividade do admin provavelmente está quebrada

O CSS mobile tenta alterar `.sidebar`, `.sidebar-logo` e `.content`, mas o HTML usa principalmente `#sidebar`, `.slogo` e `#main`. Enquanto isso, o desktop fixa `#sidebar` e aplica margem lateral permanente em `#main`.

Consequentemente, no celular a barra lateral pode continuar fixa, ocupando espaço e deixando o conteúdo deslocado. A tabela apenas ganha rolagem horizontal; isso não resolve a navegação.

#### Ajuste recomendado

- Corrigir os seletores para `#sidebar`, `.slogo` e `#main`.
- Transformar o menu lateral em drawer no mobile.
- Exibir um botão “Menu” no cabeçalho.
- Em Pedidos e Usuários, considerar cards resumidos no mobile em vez de tabelas com 10–12 colunas.
- Manter ações secundárias dentro de um menu “Mais ações”.

**Vantagem:** torna operações administrativas viáveis em celular e telas menores.

**Desvantagem:** cards mobile duplicam parcialmente a representação das tabelas e aumentam o CSS/JS a manter.

### 5. Excesso de densidade e ações perigosas no admin

As tabelas administrativas exibem muitas ações simultâneas: editar, senha, duplicar, arquivar, reativar e excluir. Isso aumenta a chance de clique errado e dificulta localizar a ação principal.

O modal de cancelamento usa “Cancelar SEM Reembolso” e “Cancelar COM Reembolso”. O texto é explícito, mas as duas ações destrutivas ficam muito próximas e com peso semelhante.

#### Proposta

- Manter uma ação primária visível.
- Agrupar ações raras em “Mais ações”.
- Apresentar nome do cliente, valor, status e ID do pedido na confirmação.
- Explicar a consequência: “cancela localmente”, “solicita reembolso no Asaas” e “revoga acesso”.
- Exigir uma segunda confirmação somente em exclusões irreversíveis ou reembolsos.
- Desabilitar o botão após o primeiro clique e mostrar progresso.

**Vantagem:** reduz erros operacionais graves.

**Desvantagem:** ações frequentes podem exigir um clique adicional.

### 6. Inconsistência editorial

Há alternância entre:

- “Premierhost”, “Premier Host” e “Premierhost”.
- “Usuários” e “Usuarios”.
- “Conversão” e “conversao”.
- “PIX” e “Pix”.
- Títulos em caixa normal e botões em caixa alta.
- “Painel”, “Painel do Cliente” e “Entrar no Painel”.

#### Guia editorial sugerido

- Marca: **Premier Host**.
- Meio de pagamento: **Pix**.
- Botões em frase normal: “Gerar Pix”, “Continuar pagamento”.
- Usar “você” em mensagens ao cliente.
- Usar verbos no infinitivo ou imperativo de maneira consistente.
- Evitar termos internos: “topologia”, “AD”, “slots” e “QR estático”.
- Padrão de erro: o que ocorreu + como resolver.
- Padrão de sucesso: resultado + próximo passo.

**Vantagem:** aumenta confiança e aparência profissional.

**Desvantagem:** exige inventário e revisão de textos no frontend e respostas da API.

## Product Analytics

Não foram encontrados Google Analytics, Matomo, Plausible, PostHog, Mixpanel, data layer ou sistema próprio de eventos. Os logs existentes são operacionais e de segurança, não uma trilha analítica de comportamento.

As métricas do admin são importantes, mas começam tarde demais: quando já existe usuário ou pedido. Atualmente, não é possível responder com precisão:

- Quantas pessoas visualizaram a landing?
- Quantas abriram o simulador?
- Quantas alteraram um plano?
- Quantas abriram o cadastro?
- Onde o cadastro foi abandonado?
- Quantas tentaram gerar Pix?
- Quantas copiaram o código?
- Qual plano converte melhor desde a simulação?
- Quais erros mais impedem a compra?
- Quanto tempo passa entre Pix gerado, pago e acesso liberado?

### Proposta recomendada: analytics first-party

Mantendo Vanilla JavaScript, Dapper e PostgreSQL, recomenda-se uma instrumentação interna simples:

```text
Landing
  → Simulador
    → Autenticação
      → Pix gerado
        → Pix pago
          → Acesso liberado
            → Renovação
```

### Eventos iniciais

- `landing_viewed`
- `plan_cta_clicked`
- `simulator_viewed`
- `simulation_changed`
- `auth_opened`
- `signup_started`
- `signup_completed`
- `email_confirmed`
- `checkout_attempted`
- `pix_created`
- `pix_copied`
- `payment_received`
- `access_delivered`
- `checkout_error`
- `renewal_started`
- `renewal_paid`

### Dimensões aceitáveis

- Período escolhido.
- Quantidade de computadores e instâncias.
- Origem/UTM.
- Visitante ou autenticado.
- Categoria do dispositivo.
- Código controlado do erro.
- Identificador anônimo de sessão.

Não registrar em analytics: e-mail, WhatsApp, AnyDesk, senha, código Pix ou outros dados pessoais desnecessários.

### Vantagens

- Dados sob controle da Premier Host.
- Menor dependência de terceiros e cookies.
- Compatibilidade com a arquitetura atual.
- Pagamento e liberação podem ser confirmados pelo backend, evitando métricas falsas.
- Possibilidade de integrar o funil ao dashboard existente.

### Desvantagens

- Será necessário criar tabela, endpoint, retenção e agregações.
- Exige filtragem de bots e eventos duplicados.
- Um dashboard próprio demanda manutenção.
- Analytics sem governança pode acumular eventos sem utilidade.

Uma ferramenta externa reduziria o tempo de implementação, mas traria dependência, revisão de LGPD, consentimento quando aplicável e alterações na Content Security Policy de `Program.cs`.

## Roadmap sugerido

### P0 — Corrigir risco e acesso

1. Corrigir responsividade do admin.
2. Revisar foco, labels, nomes acessíveis e modais.
3. Melhorar confirmações de reembolso, exclusão e revogação.
4. Padronizar feedback de erro, sucesso e carregamento.
5. Testar cliente e admin em 360 px, 768 px, desktop e navegação somente por teclado.

### P1 — Melhorar conversão

1. Trocar o CTA principal da landing para “Simular preço”.
2. Reescrever o início do painel em linguagem comercial.
3. Tornar o botão dinâmico: “Entrar para gerar Pix” para visitante e “Gerar Pix” para autenticado.
4. Explicar melhor por que o AnyDesk é solicitado.
5. Padronizar nomenclatura, capitalização e microtextos.
6. Medir o funil desde landing até pagamento.

### P2 — Evoluir a interface

1. Criar tokens e pequenos componentes compartilhados, sem introduzir framework.
2. Reduzir ações visíveis nas tabelas administrativas.
3. Criar visualização mobile em cards para operações críticas.
4. Adicionar skeletons em áreas maiores.
5. Adicionar painel de funil, erros, tempo de liberação e renovação.
6. Realizar testes moderados com 5–8 clientes e pelo menos dois operadores do admin.

## Conclusão

O projeto já demonstra preocupação real com UX, principalmente na explicação do serviço, simulação pública, feedback e prevenção de ações críticas.

O maior ganho imediato virá de:

1. Alinhar CTAs e textos ao fluxo real.
2. Corrigir acessibilidade e experiência mobile do admin.
3. Instrumentar o funil completo.

Atualmente, o produto enxerga receita, pedidos e operação, mas ainda não enxerga adequadamente o comportamento que leva o visitante até a compra. A combinação de UX Writing mais direto, correções de interface e Product Analytics permitirá reduzir atrito com base em dados, em vez de depender apenas de percepção.

## Situação da implementação

As recomendações prioritárias deste documento foram aplicadas em julho de 2026:

- CTA principal direcionado ao simulador público.
- Microtextos principais revisados no cadastro, painel, Pix e recuperação de senha.
- Labels, regiões de status, semântica e navegação por teclado reforçados.
- Modais com identificação acessível, tecla Escape, contenção e restauração de foco.
- Menu administrativo responsivo corrigido para celular.
- Confirmação de cancelamento e reembolso reescrita com consequências explícitas.
- Analytics first-party implementado com lista permitida, sanitização, retenção de 13 meses e sem dados pessoais nos eventos.
- Funil de produto incluído no Dashboard administrativo.
- Política de Privacidade atualizada para explicar a telemetria.
- Checkout complementado com identificação do servidor de WYD, validação preventiva no backend e mensagem de erro contextual.
- Tabela de Pedidos ajustada para telas estreitas, com reticências, rolagem horizontal e ID do Asaas recolhível.

Melhorias que dependem de pesquisa com usuários, como substituir definitivamente o termo “instância” em todos os contextos e reorganizar tabelas administrativas em cards, devem ser validadas com clientes e operadores antes de uma mudança estrutural maior.
