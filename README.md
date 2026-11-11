# Monitor de Bateria — Ajazz AJ139 Pro (Windows)

Utilitário leve para a bandeja do sistema (**System Tray**) do Windows 10/11 que monitora o nível de bateria e o estado de carregamento do mouse **Ajazz AJ139 Pro** via USB HID e envia uma **notificação nativa do Windows (Toast)** quando o mouse atinge **100% de carga**.

Funciona em paralelo com o software oficial da Ajazz (`OemDrv.exe`), sem conflito de acesso HID, tanto conectado pelo **cabo USB** (`VID_25A7 / PID_FA7B`) quanto pelo **dongle 2.4 GHz** (`VID_25A7 / PID_FA7C`).

---

## Funcionalidades

- **Ícone dinâmico na bandeja do sistema** com a porcentagem atual em fonte grande (`Bahnschrift` Bold):
  - **Amarelo (`CB`):** carregando (conectado no cabo USB abaixo de `100%` ou sinal de carga ativo).
  - **Azul (`100`):** conectado pelo cabo USB com carga completa (`100%`).
  - **Verde (`25`–`100`):** fora do cabo (`2.4G`), incluindo `100%` até `25%`.
  - **Vermelho (`<= 20`):** bateria baixa (`20%` ou menos).
  - **Cinza (`--`):** mouse desligado, em repouso ou desconectado.
- **Notificações Toast nativas do Windows:**
  - **Carga completa (`100%`):** notifica uma única vez por ciclo de carga ao atingir `100%` (com debounce de 2 leituras).
  - **Bateria baixa (`25%` até `0%`):** notifica quando chega a `25%` sem fio e novamente a cada queda de `5%` (`20%`, `15%`, `10%`, `5%`, `0%`).
- **Menu de contexto (clique direito no ícone):**
  - *Atualizar agora*
  - *Testar notificação*
  - *Iniciar com o Windows* (liga/desliga o registro automático)
  - *Abrir pasta de logs*
  - *Sair*

---

## Pré-requisitos

- **Windows 10 ou Windows 11** (usa o `powershell.exe` e `wscript.exe` já nativos do sistema — não exige instalar Node.js, Python ou .NET SDK, nem executáveis `.exe` não assinados).
- Mouse **Ajazz AJ139 Pro** (`VID=0x25A7`, `PID=0xFA7B` USB / `0xFA7C` dongle 2.4 GHz).

---

## Como rodar

1. Clone ou baixe este repositório em uma pasta fixa (por exemplo, `C:\Users\<SeuUsuario>\Documents\ajazz-battery-monitor`).
2. Para iniciar manualmente em segundo plano (sem janela de terminal), dê dois cliques em:
   - **`Start-AjazzMonitor.vbs`**

   Ou execute via PowerShell:
   ```powershell
   powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ".\Start-AjazzMonitor.ps1"
   ```

> **Dica (Windows 11):** Na primeira vez, o ícone pode aparecer dentro da setinha **`^` (Mostrar ícones ocultos)** perto do relógio. Basta arrastá-lo para fora para mantê-lo sempre visível na barra de tarefas.

---

## Como deixar iniciando automaticamente com o Windows

Você tem **duas formas** equivalentes (use apenas uma):

### Opção 1 — Pelo próprio menu do ícone (mais fácil)
1. Inicie o monitor dando dois cliques em **`Start-AjazzMonitor.vbs`**.
2. Clique com o botão direito no ícone da bandeja e marque **`Iniciar com o Windows`**.

Isso cria automaticamente a entrada `AjazzBatteryMonitor` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

### Opção 2 — Via comando no PowerShell
Execute dentro da pasta do projeto:

```powershell
$vbs = (Resolve-Path ".\Start-AjazzMonitor.vbs").Path
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AjazzBatteryMonitor" -Value "wscript.exe `"$vbs`""
```

Para remover da inicialização automática:

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "AjazzBatteryMonitor" -ErrorAction SilentlyContinue
```

---

## Estrutura do projeto

- `src/AjazzProtocol.cs` — codificação/decodificação dos pacotes HID de 17 bytes (`0x03` conexão e `0x04` bateria + checksum `0x55 - soma`).
- `src/HidNative.cs` — leitura USB HID via `SetupAPI` / `HID.DLL` com `FILE_SHARE_READ | FILE_SHARE_WRITE`.
- `src/BatteryMonitor.cs` — máquina de estados e debounce para notificação única ao atingir `100%`.
- `src/ToastNotifier.cs` — disparo de notificação Toast nativa via WinRT (com fallback para balão da bandeja).
- `src/TrayApp.cs` — ícone dinâmico na bandeja, menu de contexto e registro de inicialização.
- `Start-AjazzMonitor.ps1` / `Start-AjazzMonitor.vbs` — inicialização silenciosa em segundo plano com trava de instância única (`Mutex`).
- `probe/HidProbe.cs` — utilitário de diagnóstico HID.
- `docs/protocol-notes.md` — documentação técnica do protocolo obtida via engenharia reversa.
- `tests/AjazzProtocolTests.cs` & `Run-Tests.ps1` — suíte de testes unitários.

---

## Rodando os testes

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Run-Tests.ps1"
```
