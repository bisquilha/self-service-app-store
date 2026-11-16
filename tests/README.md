# Testes de API da Loja de Aplicativos

Scripts PowerShell 5.1 que chamam a API do servidor de demonstração (Tactical simulado). Rodam com a conta do Windows de quem executa (`UseDefaultCredentials`) e mandam o cabeçalho `X-Requested-With: loja-admin`.

## Subir o servidor de demonstração

Cada suíte pede uma pasta de dados **nova** (os scripts adicionam o mesmo app). Exemplo, no PC de desenvolvimento:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'          # liga o Tactical simulado e AdminGroups = "*"
$env:Loja__DataDir          = 'C:\temp\loja-dados'   # pasta nova e vazia
$env:Loja__InternalFeedPath = 'C:\temp\loja-pacotes' # com os .nupkg de teste (app-interno, teste-falha)
dotnet run --project ..\src\Loja.Server\Loja.Server.csproj
```

Escuta em `http://localhost:5080`. O PC precisa estar no domínio para as checagens que consultam o AD.

## Rodar todas de uma vez

```powershell
.\run-suites.ps1 -Feed C:\temp\loja-pacotes
```

Para cada suíte apaga e recria a pasta de dados, sobe o servidor de demonstração (porta 5080), roda o script e para o servidor; no fim
imprime `OK`, `FALHA` e `PULOU` por suíte (a saída completa de cada uma fica em `-Temp`). `-Only test-programas,test-saude-catalogo` roda
só algumas. Precisa de PC no domínio, internet, `python` no PATH e de uma pasta `-Feed` com os `.nupkg` de teste (`app-interno` e `teste-falha`).

## Suítes

| Script | O que cobre | Observação |
|---|---|---|
| `test-api.ps1` | Fase 1: login, travas de máquina, catálogo, instalar, histórico, callback | Precisa de internet (busca no Chocolatey) |
| `test-fase2.ps1` | Estado de instalado, atualizar, desinstalar, atualização automática | Pasta de dados nova |
| `test-configuracoes.ps1 -DataDir <pasta>` | Faixas dos parâmetros e salvar; envio do `.exe` do app: inspecionar sem publicar, publicar, versão já publicada, mais antiga que a atual, apagar, e o cabeçalho anti-CSRF | Pasta de dados **nova** (as versões publicadas ficam no banco). Usa os `.exe` `Downloads\LojaDeAplicativos-1.2.0.exe` e `-1.2.1.exe` (`-Exe120` e `-Exe121` trocam o caminho); sem eles, pula o envio |
| `test-grupos.ps1 -DataDir <pasta>` | Busca de grupos do AD e validação de grupos ao salvar apps | Sem `-DataDir`, pula o teste do "grupo antigo" (precisa de `python` no PATH) |
| `test-historico.ps1 -DataDir <pasta>` | Histórico e Visão geral do painel: 60 pedidos de mentira gravados direto no banco, ordenação no servidor por todas as colunas (nas várias páginas e no CSV), valores inválidos de `sort`/`dir`, filtros (situação, inclusive várias de uma vez, ação, computador, texto, datas), paginação, CSV, código de saída e os números da visão geral batendo com o histórico | Pasta de dados **nova**; precisa do `python` no PATH. Sem `-DataDir`, pula tudo |

| `test-grupos-sid.ps1 -DataDir <pasta>` | Grupos por SID: gravar SID e nome, acesso pelo SID, renomear no AD sem perder acesso, migração de grupos antigos (só nome), AD fora do ar x grupo inexistente, grupos antigos que sumiram do AD | Pasta de dados **nova**; precisa do `python`. Usa o AD de verdade (só leitura) |
| `test-auditoria.ps1 -DataDir <pasta>` | Trilha de auditoria: o que cada alteração grava (antes e depois), filtros, paginação, "Desfazer" marcado como revertido, e que auditar nunca derruba a alteração | Pasta de dados **nova** |
| `test-lote-grupos.ps1 -DataDir <pasta>` | Edição de grupos em lote (atômica), Desfazer, recusas (grupo inexistente, app sem grupo, "Todos"), tela Grupos (detalhe de um grupo, grupos aninhados), filtro por grupo do usuário no Histórico, no CSV e em Máquinas | Pasta de dados **nova**; sem `-DataDir` pula o filtro no Histórico. Usa o AD de verdade (só leitura) |
| `test-programas.ps1 -DataDir <pasta>` | Programas do Windows fora da loja: nome exato (e não por semelhança), sem duplicar com o Chocolatey, estados de versão (menor, igual, maior, não comparável, com sufixo), validação dos nomes, auditoria, busca de nomes, `/updates` e o **alcance de "Atualizar em todas"** (só máquinas online e estações; funcionário não alcança várias máquinas), atualização automática só com versões comparáveis | Pasta de dados **nova**; precisa do `python` e de `-DataDir`. Internet (pacotes vlc, 7zip, putty) |
| `test-programas-callback.ps1 -LogFile <arquivo>` | Contrato do retorno do script: script antigo (sem programas), script novo com a **lista real deste PC** (roda a função `Get-WindowsPrograms` de `tactical\Loja-Choco.ps1`, só leitura), texto hostil, limites (200, 50, 2.000), data da coleta, token de uso único | Servidor em **retorno manual**: `$env:Loja__Tactical__MockManualCallback='true'` e a saída do servidor num arquivo (`-LogFile`), de onde sai o token de cada pedido |
| `test-maquinas-reexecucao.ps1 -DataDir <pasta>` | Reexecutar pedido que falhou (vira pedido novo do painel, ligado ao original; revalida app, desinstalação liberada e máquina), recusas, "Verificar programas" em várias máquinas, último contato dos agentes | Pasta de dados **nova**; precisa do `python` e do pacote interno `teste-falha` no feed |
| `test-saude-catalogo.ps1 -DataDir <pasta>` | `catalog-health`: ativo sem grupo, grupo inexistente no AD, grupo com 0 membros diretos (pulado se não houver um no AD), desatualizado em muitas máquinas (o critério exato: 5, ou 2 e 20%) | Pasta de dados **nova**; precisa do `python`. Usa o AD de verdade (só leitura). "O AD não respondeu" não dá para simular aqui |
| `test-feed-diagnostico.ps1 -Esperado <acesso\|naoachou\|semnome\|ok>` | Diagnóstico do cartão "Feed interno (NAS)": o que foi observado separado da hipótese, conta do servidor, e que nada é alterado | O servidor precisa apontar para um caminho que não abre (veja o cabeçalho do script para o `InternalFeedPath` de cada cenário). `acesso` deve rodar sem elevar |

`seed-demo.ps1 -DataDir <pasta>` não é teste: cadastra aplicativos de exemplo (internet e internos, com grupos reais do AD), desativa um, instala dois de forma simulada neste PC e, com `-DataDir`, deixa um grupo que não existe mais no AD em um deles. Serve para conferir as telas do painel. Precisa de internet e de pasta de dados nova.

Cada script imprime `OK`, `FALHA` ou `PULOU` por verificação e termina com o número de falhas como código de saída.

## Fora desta pasta

O cenário "usuário comum" da Fase 2 (servidor reiniciado com `AdminGroups` apontando para um grupo inexistente e apps semeados direto no banco) era montado à mão e não está nestes scripts. A API não aceita mais criar app com grupo inexistente, então esse cenário precisa ser semeado por SQL.

`contraste-app.py` confere o contraste (WCAG) das cores dos temas claro e escuro do app WPF (`src\Loja.App\Themes`): `python -I tests\contraste-app.py`. Não precisa de servidor. O app em si não tem teste automatizado; a conferência da 1.3.0 foi feita abrindo o app contra o servidor de demonstração e lendo a janela pelo UI Automation (veja `docs\IMPLANTACAO.md`, 9.6).
