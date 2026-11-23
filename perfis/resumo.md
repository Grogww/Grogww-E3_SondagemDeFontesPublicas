# Perfil das execuções da sondagem

Gerado em 2026-11-23 20:22 a partir de `dados/`, sem acesso à rede. Detalhes completos nos CSV desta pasta.

**Como ler:**
- **Requisição** é a tentativa final de cada chamada; repetições por 5xx ou timeout aparecem em *retentativas*.
- **Vazio** é resposta sem dados sinalizada pela fonte: 204, ou o status listado em `config/perfil.json` (ex.: 404 "Não há contrato publicado" no PNCP). Não conta como erro.
- **Taxa de erro** = (4xx não vazios + 5xx + falhas de rede + 429) ÷ requisições.
- **p50/p95** da latência total (até o fim do corpo) de todas as tentativas com resposta HTTP, por interpolação linear (igual ao PERCENTIL.INC). Em downloads, a latência inclui a transferência do arquivo.
- **Preenchido** = valor não nulo, não vazio e diferente dos marcadores ("Sem informação", "Sem Informação", "Não informado", "Não Informado"). Em listas, o registro conta como preenchido se algum elemento tiver valor.
- **Registros de SC** dependem do recorte de cada endpoint (coluna *recorte*); a *nota* diz a que UF o campo se refere.

## cndt · 20261122-222327

- Situação: **concluida** · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:23 · duração 0 min · 2 tentativas
- Ritmo: limite declarado nenhum (base 60/min), configurado 36/min, observado 38,3/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| pagina-apresentacao | 1 | 1 | 0 | 0 | 0,0% | 241,6 | 241,6 | 200×1 |
| pagina-emissao | 1 | 1 | 0 | 0 | 0,0% | 344 | 344 | 200×1 |
| **total** | 2 | 2 | 0 | 0 | 0,0% | 292,8 | 338,9 | |

Respostas em HTML ou texto, sem registros para perfilar: pagina-apresentacao, pagina-emissao.

## cnpj · 20261122-222329

- Situação: **concluida** · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:23 · duração 0,1 min · 5 tentativas
- Ritmo: limite declarado nenhum (base 60/min), configurado 36/min, observado 35,9/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| listagem-cnpj | 2 | 2 | 0 | 0 | 0,0% | 253,4 | 253,8 | 200×2 |
| listagem-ecf | 1 | 1 | 0 | 0 | 0,0% | 202,8 | 202,8 | 200×1 |
| listagem-raiz | 1 | 1 | 0 | 0 | 0,0% | 397,2 | 397,2 | 200×1 |
| pagina-conjunto | 1 | 1 | 0 | 0 | 0,0% | 201,6 | 201,6 | 200×1 |
| **total** | 5 | 5 | 0 | 0 | 0,0% | 252,9 | 368,5 | |

Respostas em HTML ou texto, sem registros para perfilar: listagem-cnpj, listagem-ecf, listagem-raiz, pagina-conjunto.

## pncp · 20261122-222915

- Situação: **interrompida-429** (HTTP 429 em contratacoes-publicacao) · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:29 · duração 0,4 min · 15 tentativas
- Ritmo: limite declarado nenhum (base 60/min), configurado 36/min, observado 36,1/min
- Cabeçalhos de limite nas respostas: nenhum
- **429** em `contratacoes-publicacao` na 15ª tentativa, 23,3 s após o início, com 15 tentativas nos 60 s anteriores; Retry-After: ausente

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| contratacoes-publicacao | 7 | 5 | 1 | 1 | 14,3% | 397,6 | 749,9 | 200×5 204×1 429×1 |
| dominio-modalidades | 1 | 1 | 0 | 0 | 0,0% | 37 | 37 | 200×1 |
| dominio-modos-disputas | 1 | 1 | 0 | 0 | 0,0% | 34,3 | 34,3 | 200×1 |
| dominio-naturezas-juridicas | 1 | 1 | 0 | 0 | 0,0% | 21,4 | 21,4 | 200×1 |
| dominio-portes-empresa | 1 | 1 | 0 | 0 | 0,0% | 19,9 | 19,9 | 200×1 |
| dominio-situacoes-resultado | 1 | 1 | 0 | 0 | 0,0% | 21,1 | 21,1 | 200×1 |
| dominio-tipos-contratos | 1 | 1 | 0 | 0 | 0,0% | 35,7 | 35,7 | 200×1 |
| swagger | 2 | 2 | 0 | 0 | 0,0% | 338,8 | 463,4 | 200×2 |
| **total** | 15 | 13 | 1 | 1 | 6,7% | 163,8 | 715,9 | |

| Endpoint | Registros | SC | % SC | Recorte | Campos | 100% preenchidos | Nunca preenchidos |
|---|--:|--:|--:|---|--:|--:|---|
| contratacoes-publicacao | 177 | 177 | 100,0% | campo (`unidadeOrgao.ufSigla`); filtro na origem: 100,0% de SC | 53 | 39 | `orgaoSubRogado`, `unidadeSubRogada` |
| dominio-modalidades | 20 | — | — | nao-se-aplica | 7 | 7 | — |
| dominio-modos-disputas | 6 | — | — | nao-se-aplica | 6 | 6 | — |
| dominio-naturezas-juridicas | 96 | — | — | nao-se-aplica | 6 | 5 | — |
| dominio-portes-empresa | 6 | — | — | nao-se-aplica | 6 | 5 | — |
| dominio-situacoes-resultado | 4 | — | — | nao-se-aplica | 2 | 2 | — |
| dominio-tipos-contratos | 12 | — | — | nao-se-aplica | 6 | 6 | — |

## pncp · 20261122-223518

- Situação: **concluida** · 1 segmento(s) · commit 6fddfe4
- Início 22/11/2026 22:35 · duração 49,5 min · 560 tentativas
- Ritmo: limite declarado 20/min, configurado 12/min, observado 11,3/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| atas | 3 | 3 | 0 | 0 | 0,0% | 1906,5 | 11877 | 200×3 |
| atas-atualizacao | 2 | 2 | 0 | 0 | 0,0% | 305,3 | 356,7 | 200×2 |
| compra-atas | 85 | 7 | 78 | 0 | 0,0% | 36,4 | 88,4 | 200×7 204×78 |
| compra-contratos | 85 | 20 | 65 | 0 | 0,0% | 47,1 | 89,7 | 200×20 404×65 |
| compra-item-resultados | 90 | 90 | 0 | 0 | 0,0% | 32,4 | 71,4 | 200×90 |
| compra-itens | 85 | 85 | 0 | 0 | 0,0% | 46,2 | 164,7 | 200×85 |
| contratacao-chave | 85 | 85 | 0 | 0 | 0,0% | 43,3 | 419,6 | 200×85 |
| contratacoes-atualizacao | 6 | 6 | 0 | 0 | 0,0% | 580,7 | 1692,5 | 200×6 |
| contratacoes-proposta | 5 | 5 | 0 | 0 | 0,0% | 459,3 | 2082,2 | 200×5 |
| contratacoes-publicacao | 53 | 50 | 3 | 0 | 0,0% | 668,9 | 2363,8 | 200×50 204×3 |
| contrato-chave | 20 | 20 | 0 | 0 | 0,0% | 49,7 | 92,2 | 200×20 |
| contratos | 5 | 5 | 0 | 0 | 0,0% | 673,6 | 1094,4 | 200×5 |
| contratos-atualizacao | 2 | 2 | 0 | 0 | 0,0% | 477,5 | 503 | 200×2 |
| dominio-modalidades | 1 | 1 | 0 | 0 | 0,0% | 62,8 | 62,8 | 200×1 |
| dominio-modos-disputas | 1 | 1 | 0 | 0 | 0,0% | 28,7 | 28,7 | 200×1 |
| dominio-naturezas-juridicas | 1 | 1 | 0 | 0 | 0,0% | 72,9 | 72,9 | 200×1 |
| dominio-portes-empresa | 1 | 1 | 0 | 0 | 0,0% | 35,7 | 35,7 | 200×1 |
| dominio-situacoes-resultado | 1 | 1 | 0 | 0 | 0,0% | 26,4 | 26,4 | 200×1 |
| dominio-tipos-contratos | 1 | 1 | 0 | 0 | 0,0% | 28,4 | 28,4 | 200×1 |
| instrumentos-cobranca | 3 | 3 | 0 | 0 | 0,0% | 8256,7 | 19593,2 | 200×3 |
| orgao | 10 | 10 | 0 | 0 | 0,0% | 54,4 | 70,7 | 200×10 |
| orgao-unidades | 10 | 10 | 0 | 0 | 0,0% | 61,5 | 87,4 | 200×10 |
| pca-atualizacao | 1 | 0 | 0 | 1 | 100,0% | 50428,7 | 50580,5 | 500×1 |
| swagger | 2 | 2 | 0 | 0 | 0,0% | 347,5 | 464,1 | 200×2 |
| **total** | 558 | 411 | 146 | 1 | 0,2% | 45,7 | 892,7 | |

| Endpoint | Registros | SC | % SC | Recorte | Campos | 100% preenchidos | Nunca preenchidos |
|---|--:|--:|--:|---|--:|--:|---|
| atas | 1500 | — | — | sem-uf | 24 | 18 | — |
| atas-atualizacao | 1000 | — | — | sem-uf | 24 | 18 | — |
| compra-atas | 59 | 59 | 100,0% | campo (`unidadeOrgao.ufSigla`) | 32 | 28 | `dataCancelamento`, `orgaoSubRogado`, `unidadeSubRogada` |
| compra-contratos | 24 | 24 | 100,0% | campo (`unidadeOrgao.ufSigla`) | 60 | 45 | `identificadorCipi`, `informacaoComplementar`, `niFornecedorSubContratado`, `nomeFornecedorSubContratado`, `orgaoParteEnvolvida`, `orgaoSubRogado`, `tipoParteEnvolvida`, `tipoPessoaSubContratada` (+3) |
| compra-item-resultados | 117 | 19 | 16,2% | campo (`localidadeFornecedor.uf`) | 47 | 24 | `amparoLegalMargemPreferencia`, `dataCancelamento`, `dataCotacaoMoedaEstrangeira`, `localidadeExterior`, `moedaEstrangeira`, `motivoCancelamento`, `paisOrigemProdutoServico`, `timezoneCotacaoMoedaEstrangeira` (+1) |
| compra-itens | 898 | 898 | 100,0% | herdado | 49 | 24 | `ncmNbsDescricao`, `percentualMargemPreferenciaAdicional`, `percentualMargemPreferenciaNormal`, `tipoMargemPreferencia` |
| contratacao-chave | 85 | 85 | 100,0% | campo (`unidadeOrgao.ufSigla`) | 56 | 40 | `orgaoSubRogado`, `unidadeSubRogada` |
| contratacoes-atualizacao | 300 | 300 | 100,0% | campo (`unidadeOrgao.ufSigla`); filtro na origem: 100,0% de SC | 53 | 37 | `orgaoSubRogado`, `unidadeSubRogada` |
| contratacoes-proposta | 250 | 250 | 100,0% | campo (`unidadeOrgao.ufSigla`); filtro na origem: 100,0% de SC | 53 | 39 | `emendaParlamentar`, `orgaoSubRogado`, `unidadeSubRogada` |
| contratacoes-publicacao | 2273 | 2273 | 100,0% | campo (`unidadeOrgao.ufSigla`); filtro na origem: 100,0% de SC | 53 | 37 | `orgaoSubRogado`, `unidadeSubRogada` |
| contrato-chave | 20 | 20 | 100,0% | campo (`unidadeOrgao.ufSigla`) | 60 | 45 | `identificadorCipi`, `informacaoComplementar`, `niFornecedorSubContratado`, `nomeFornecedorSubContratado`, `orgaoParteEnvolvida`, `orgaoSubRogado`, `tipoParteEnvolvida`, `tipoPessoaSubContratada` (+4) |
| contratos | 2500 | 689 | 27,6% | campo (`unidadeOrgao.ufSigla`) | 65 | 41 | `niFornecedorSubContratado`, `nomeFornecedorSubContratado`, `tipoPessoaSubContratada` |
| contratos-atualizacao | 1000 | 163 | 16,3% | campo (`unidadeOrgao.ufSigla`) | 65 | 39 | `niFornecedorSubContratado`, `nomeFornecedorSubContratado`, `tipoPessoaSubContratada` |
| dominio-modalidades | 20 | — | — | nao-se-aplica | 7 | 7 | — |
| dominio-modos-disputas | 6 | — | — | nao-se-aplica | 6 | 6 | — |
| dominio-naturezas-juridicas | 96 | — | — | nao-se-aplica | 6 | 5 | — |
| dominio-portes-empresa | 6 | — | — | nao-se-aplica | 6 | 5 | — |
| dominio-situacoes-resultado | 4 | — | — | nao-se-aplica | 2 | 2 | — |
| dominio-tipos-contratos | 12 | — | — | nao-se-aplica | 6 | 6 | — |
| instrumentos-cobranca | 300 | 6 | 2,0% | campo (`recuperarContratoDTO.unidadeOrgao.ufSigla`) | 118 | 56 | `notaFiscalEletronica.eventos`, `recuperarContratoDTO.identificadorCipi`, `recuperarContratoDTO.niFornecedorSubContratado`, `recuperarContratoDTO.nomeFornecedorSubContratado`, `recuperarContratoDTO.tipoPessoaSubContratada`, `recuperarContratoDTO.urlCipi` |
| orgao | 10 | 10 | 100,0% | herdado | 16 | 8 | `dataSituacaoCadastral`, `descricaoNaturezaJuridica`, `justificativaAtualizacao`, `motivoSituacaoCadastral`, `nomeFantasia`, `situacaoCadastral` |
| orgao-unidades | 167 | 167 | 100,0% | campo (`municipio.uf.siglaUF`) | 25 | 22 | `orgao.cnpjEnteResponsavel` |

## portal-transparencia · 20261122-222619

- Situação: **concluida** · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:26 · duração 0,7 min · 166 tentativas
- Ritmo: limite declarado 400/min, configurado 240/min, observado 227,5/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| ceis | 24 | 24 | 0 | 0 | 0,0% | 207 | 213,8 | 200×24 |
| ceis-cnpj | 27 | 27 | 0 | 0 | 0,0% | 185,4 | 236,4 | 200×27 |
| ceis-id | 10 | 10 | 0 | 0 | 0,0% | 39,4 | 40 | 200×10 |
| cepim | 9 | 9 | 0 | 0 | 0,0% | 63,2 | 101,2 | 200×9 |
| cepim-cnpj | 27 | 27 | 0 | 0 | 0,0% | 41,7 | 63,7 | 200×27 |
| cepim-id | 10 | 10 | 0 | 0 | 0,0% | 36 | 65,8 | 200×10 |
| cnep | 21 | 21 | 0 | 0 | 0,0% | 216,7 | 269,4 | 200×21 |
| cnep-cnpj | 27 | 27 | 0 | 0 | 0,0% | 95,5 | 120 | 200×27 |
| cnep-id | 10 | 10 | 0 | 0 | 0,0% | 40 | 61,1 | 200×10 |
| swagger | 1 | 1 | 0 | 0 | 0,0% | 248,8 | 248,8 | 200×1 |
| **total** | 166 | 166 | 0 | 0 | 0,0% | 100,7 | 223,3 | |

| Endpoint | Registros | SC | % SC | Recorte | Campos | 100% preenchidos | Nunca preenchidos |
|---|--:|--:|--:|---|--:|--:|---|
| ceis | 342 | 15 | 4,4% | campo (`orgaoSancionador.siglaUf`) | 41 | 24 | — |
| ceis-cnpj | 26 | 15 | 57,7% | campo (`orgaoSancionador.siglaUf`) | 41 | 32 | `informacoesAdicionaisDoOrgaoSancionador`, `linkPublicacao`, `pessoa.cpfFormatado`, `pessoa.numeroInscricaoSocial` |
| ceis-id | 10 | 10 | 100,0% | campo (`orgaoSancionador.siglaUf`) | 38 | 25 | `detalhamentoPublicacao`, `fundamentacao`, `informacoesAdicionaisDoOrgaoSancionador`, `linkPublicacao`, `pessoa.numeroInscricaoSocial` |
| cepim | 116 | — | — | sem-uf | 26 | 21 | `pessoaJuridica.cpfFormatado`, `pessoaJuridica.numeroInscricaoSocial` |
| cepim-cnpj | 10 | — | — | sem-uf | 26 | 23 | `pessoaJuridica.cpfFormatado`, `pessoaJuridica.numeroInscricaoSocial` |
| cepim-id | 10 | 10 | 100,0% | herdado | 26 | 23 | `pessoaJuridica.cpfFormatado`, `pessoaJuridica.numeroInscricaoSocial` |
| cnep | 300 | 11 | 3,7% | campo (`orgaoSancionador.siglaUf`) | 42 | 25 | — |
| cnep-cnpj | 14 | 14 | 100,0% | campo (`orgaoSancionador.siglaUf`) | 42 | 31 | `linkPublicacao`, `pessoa.cpfFormatado`, `pessoa.numeroInscricaoSocial` |
| cnep-id | 10 | 10 | 100,0% | campo (`orgaoSancionador.siglaUf`) | 39 | 25 | `detalhamentoPublicacao`, `fundamentacao`, `linkPublicacao`, `pessoa.numeroInscricaoSocial` |

## sicaf · 20261122-222919

- Situação: **concluida** · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:29 · duração 5 min · 157 tentativas
- Ritmo: limite declarado nenhum (base 60/min), configurado 36/min, observado 31/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| arp-itens-fornecedor | 20 | 20 | 0 | 0 | 0,0% | 108 | 495,7 | 200×20 |
| contratos | 5 | 5 | 0 | 0 | 0,0% | 1121,4 | 2730,5 | 200×5 |
| contratos-fim-vigencia | 5 | 5 | 0 | 0 | 0,0% | 3172 | 9928,1 | 200×5 |
| contratos-fornecedor | 10 | 10 | 0 | 0 | 0,0% | 590,9 | 1505,5 | 200×10 |
| contratos-itens | 5 | 5 | 0 | 0 | 0,0% | 1957,1 | 12317,4 | 200×5 |
| fornecedores | 45 | 45 | 0 | 0 | 0,0% | 241,9 | 465,4 | 200×45 |
| fornecedores-cnpj | 30 | 30 | 0 | 0 | 0,0% | 235 | 406,8 | 200×30 |
| fornecedores-filtro | 14 | 14 | 0 | 0 | 0,0% | 265 | 373,5 | 200×14 |
| resultados-fornecedor | 20 | 20 | 0 | 0 | 0,0% | 224,7 | 672,8 | 200×20 |
| swagger | 1 | 1 | 0 | 0 | 0,0% | 308 | 308 | 200×1 |
| uasg | 2 | 2 | 0 | 0 | 0,0% | 129,5 | 144,9 | 200×2 |
| **total** | 157 | 157 | 0 | 0 | 0,0% | 251,1 | 1716,3 | |

| Endpoint | Registros | SC | % SC | Recorte | Campos | 100% preenchidos | Nunca preenchidos |
|---|--:|--:|--:|---|--:|--:|---|
| arp-itens-fornecedor | 2 | — | — | sem-uf | 34 | 33 | `dataHoraExclusao` |
| contratos | 58 | — | — | sem-uf | 38 | 28 | `codigoSubcategoria`, `nomeSubcategoria`, `totalDespesasAcessorias` |
| contratos-fim-vigencia | 89 | — | — | sem-uf | 38 | 30 | `codigoSubcategoria`, `dataHoraExclusao`, `nomeSubcategoria`, `totalDespesasAcessorias` |
| contratos-fornecedor | 16 | — | — | sem-uf | 38 | 32 | `codigoSubcategoria`, `nomeSubcategoria`, `totalDespesasAcessorias` |
| contratos-itens | 94 | — | — | sem-uf | 35 | 29 | — |
| fornecedores | 22500 | 1032 | 4,6% | campo (`ufSigla`) | 13 | 3 | — |
| fornecedores-cnpj | 30 | 23 | 76,7% | campo (`ufSigla`) | 13 | 12 | `cpf` |
| fornecedores-filtro | 7000 | 330 | 4,7% | campo (`ufSigla`) | 13 | 12 | `cpf` |
| resultados-fornecedor | 12 | 0 | 0,0% | campo (`ufFornecedor`) | 43 | 30 | `amparoLegalCriterioDesempateId`, `amparoLegalCriterioDesempateNome`, `amparoLegalMargemPreferenciaId`, `amparoLegalMargemPreferenciaNome`, `dataCancelamentoPncp`, `dataCotacaoMoedaEstrangeira`, `moedaEstrangeiraId`, `motivoCancelamento` (+4) |
| uasg | 779 | 779 | 100,0% | campo (`siglaUf`); filtro na origem: 100,0% de SC | 22 | 15 | — |

## transparencia-sc · 20261122-222340

- Situação: **concluida** · 1 segmento(s) · commit 6151cb7
- Início 22/11/2026 22:23 · duração 0,5 min · 17 tentativas
- Ritmo: limite declarado nenhum (base 60/min), configurado 36/min, observado 32,5/min
- Cabeçalhos de limite nas respostas: nenhum

| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| dados-abertos | 2 | 1 | 0 | 1 | 50,0% | 140,4 | 200,5 | 200×1 500×1 |
| dados-abertos-despesa | 1 | 0 | 0 | 1 | 100,0% | 79,7 | 79,7 | 404×1 |
| despesa-indicador | 5 | 0 | 0 | 5 | 100,0% | 50,8 | 76,9 | 404×5 |
| despesa-mensal | 5 | 4 | 0 | 1 | 20,0% | 344,1 | 394,5 | 200×4 404×1 |
| dicionario-despesa | 1 | 1 | 0 | 0 | 0,0% | 266 | 266 | 200×1 |
| pagina-inicial | 1 | 1 | 0 | 0 | 0,0% | 243,3 | 243,3 | 200×1 |
| **total** | 15 | 7 | 0 | 8 | 53,3% | 110,2 | 391,1 | |

| Endpoint | Registros | SC | % SC | Recorte | Campos | 100% preenchidos | Nunca preenchidos |
|---|--:|--:|--:|---|--:|--:|---|
| despesa-mensal | 186812 | 186812 | 100,0% | fonte | 60 | 59 | — |

Respostas em HTML ou texto, sem registros para perfilar: dados-abertos, pagina-inicial.

