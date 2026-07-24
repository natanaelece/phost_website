# Validação do Meta Pixel e Conversions API

Este procedimento não autoriza cobrança, liberação de computador, mensagem
externa, deploy ou reinicialização. Use-o depois da revisão, em uma janela
operacional e com ações de negócio seguras ou dados controlados.

## Configuração

O serviço lê `META_DATASET_ID`, `META_CAPI_ACCESS_TOKEN`,
`META_CAPI_TEST_EVENT_CODE` e `META_GRAPH_API_VERSION` do arquivo protegido de
ambiente. Não copie valores para o repositório, terminal compartilhado, HTML,
JavaScript, captura de tela ou chamado.

Durante a validação, `META_CAPI_TEST_EVENT_CODE=TEST30146` faz a CAPI encaminhar
os eventos para a área de teste. Remova essa variável depois da aprovação para
que eventos normais não continuem classificados como teste. Alterar o arquivo
protegido ou reiniciar o serviço exige autorização e janela operacional.

## Conferência na tela da Meta

1. Abra o **Gerenciador de Eventos** da Meta e selecione o conjunto de dados
   correspondente ao `META_DATASET_ID`.
2. Abra **Testar eventos** e mantenha a lista de eventos recebidos visível.
3. Em uma janela anônima do navegador, abra `https://phost.pro/` com as
   ferramentas de rede abertas. Antes de escolher marketing, confirme que não
   há requisições para `connect.facebook.net` nem `www.facebook.com/tr`.
4. Clique em **Recusar**. Navegue pela landing, painel, guia e privacidade e
   confirme novamente que não houve carregamento ou evento da Meta.
5. Use **Gerenciar cookies**, aceite marketing e recarregue as quatro páginas
   públicas. Confirme `PageView` e, em `/guia-wyd`, `ViewContent`.
6. Clique em um link de atendimento pelo WhatsApp e no canal de novidades.
   Confirme eventos `Contact` com `content_name` coerente, sem texto da mensagem
   ou número informado pelo visitante.
7. Registre uma solicitação de teste elegível e confirme um único `Lead`.
   Marque a solicitação como liberada somente em cenário controlado e confirme
   um único `StartTrial`.
8. Confirme uma conta controlada por e-mail e verifique um único
   `CompleteRegistration`.
9. Em ambiente seguro do Asaas, gere um Pix sem pagá-lo e confirme um único
   `InitiateCheckout`, com `BRL`, valor do backend, ID do pedido, quantidade de
   computadores, `content_ids` e `contents`.
10. Para `Purchase`, use exclusivamente um webhook controlado autorizado que
    represente pagamento realmente recebido. Reenvie o mesmo webhook e confirme
    que somente um `Purchase` aparece, com `event_id` determinístico e valor
    efetivamente pago.
11. Nos eventos híbridos, abra os detalhes e confira que navegador e servidor
    compartilham exatamente `event_name` e `event_id`, resultando em
    deduplicação.
12. Revogue em **Gerenciar cookies**, recarregue e confirme que novos eventos
    Pixel/CAPI não aparecem e que cadastro, login, teste e simulador continuam
    funcionais.

Respostas automatizadas e testes unitários usam `HttpMessageHandler` simulado e
nunca chamam a Graph API. A Dataset Quality API e um dashboard próprio ficam
para uma entrega posterior.
