# Notas do Protocolo USB HID — Ajazz AJ139 Pro (`OemDrv.exe`)

Mapeamento realizado com **REA (`rea-agents@6.8.0`) + Ghidra 12.1.4** sobre cópia isolada de `C:\Program Files (x86)\AJAZZ\AJ139 PRO Gaming Mouse\OemDrv.exe` (SHA-256 `4d10b4992ae37163b2296f3c78ab7f7b1f050168c42a525d14e32302c794e714`) e validado no hardware real conectado ao Windows.

---

## 1. Identificadores USB e Endpoints HID (Visto)

- **Vendor ID (VID):** `0x25A7` (`Cfg.ini`)
- **Product IDs (PID):**
  - `0xFA7B` — `2.4G Wireless Mouse&KeyBoard` (mouse conectado via cabo USB; verificado via `DEVPKEY_Device_BusReportedDeviceDesc` e `Cfg.ini` `PID=0xFA7B`)
  - `0xFA7C` — `2.4G Wireless Receiver` (dongle receptor 2.4 GHz; verificado via `DEVPKEY_Device_BusReportedDeviceDesc` e `Cfg.ini` `PID2=0xFA7C`)
- **Coleções HID utilizadas por PID (descobertas via `SetupAPI` / `HidP_GetCaps`):**
  - **Envio de comando (Feature Report):** `MI_01&COL07` — `UsagePage = 0xFF02`, `Usage = 0x0002`, `FeatureReportByteLength = 17`
  - **Recebimento de resposta (Input Report):** `MI_01&COL05` — `UsagePage = 0xFF01`, `Usage = 0x0000`, `InputReportByteLength = 17`
- **Coexistência com `OemDrv.exe`:** Ambas as coleções aceitam `CreateFile` com `FILE_SHARE_READ | FILE_SHARE_WRITE` enquanto `OemDrv.exe` está em execução.

---

## 2. Formato do Comando `GetPower` (Visto)

Referências: `FUN_0043be40` (`0x43be40.c`), `FUN_004524f0` (`0x4524f0.c`), `FUN_00452600` (`0x452600.c`).

O comando é enviado via `HidD_SetFeature(hFeature, buf, 17)`:

| Offset | Valor | Significado | Evidência |
|---|---|---|---|
| `0` | `0x08` | Report ID de saída/feature | `0x452600.c:30` (`*unaff_EDI = 8`) |
| `1` | `0x04` | Opcode `GetPower` | `0x4524f0.c:52,59` (`uStack_38 = 4`) |
| `2..15` | `0x00` | Zeros | `0x4524f0.c:48-56` |
| `16` | `0x49` | Checksum: `(0x55 - sum(buf[0..15])) & 0xFF` | `0x452600.c:43` (`(('U' - cVar1) - cVar5) - cVar4`) |

Pacote exato em bytes (17 bytes):
`08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49`

---

## 3. Formato da Resposta `GetPower` (Visto + Validado em Hardware)

Referências: `FUN_00450bf0` (`0x450bf0.c`), `FUN_00450960` (`0x450960.c`), `FUN_00450af0` (`0x450af0.c`), `FUN_004524f0` (`0x4524f0.c`), `FUN_004472b0` (`0x4472b0.c`), `FUN_00447410` (`0x447410.c`).

Lido via `ReadFile(hInput, inBuf, 17, ...)` (com `FILE_FLAG_OVERLAPPED`) na coleção `UsagePage = 0xFF01`:

| Offset (`inBuf`) | Valor observado | Significado | Evidência |
|---|---|---|---|
| `0` | `0x09` | Report ID de resposta | `0x450bf0.c:63` (`uStack_20 = 9`) |
| `1` | `0x04` | Eco do Opcode `GetPower` | `0x450af0.c:60` (`*(param_2 + 4) == 4`) |
| `2` | `0x00` | Status (`0x00` = OK) | `0x4524f0.c:60` (`cStack_47 == '\0'`) |
| `3..4` | `0x00` | Reservado / desconhecido | Observado no probe |
| `5` | `0x02` | Tamanho do payload (`2` bytes) | Inferido a partir do probe (`02 64 00`) |
| `6` | `0x00..0x64` | **`nPower`** — porcentagem da bateria (`0..100`) | `0x43be40.c:26` (`local_4 < 0x65`), `0x447410.c:214` (`L"%d %%"`) |
| `7` | `0x00` ou `0x01` | **`bCharging`** — estado de carregamento (`1` = carregando, `0` = não carregando) | `0x43be40.c:25,33`, `0x447410.c:182,194` |
| `8..15` | `0x00` | Padding | Observado no probe |
| `16` | `(0x55 - sum(inBuf[0..15])) & 0xFF` | Checksum (mesma fórmula do envio) | Validado no probe (`0xE2` para soma `0x73`) |

Exemplo real capturado pelo `HidProbe`:
`09-04-00-00-00-02-64-00-00-00-00-00-00-00-00-00-E2` → `nPower = 100%`, `bCharging = 0`.

---

## 4. Limites e Comportamento de Carga Completa (Visto / Inferido)

- **Visto em `0x447410.c:194`:** A UI do `OemDrv.exe` exibe a porcentagem ao atingir `bCharging == 1 && nPower == 100`.
- **Visto em `0x4472b0.c:23-34`:** Há também um caminho para `bCharging == 1 && nPower == 1` em firmwares que usam `nPower = 1` como sinal de carga cheia na animação de barra, embora `0x447410.c:194` e o hardware real reportem `nPower = 100` (`0x64`).
- **Observado no hardware real:** Com o mouse conectado via USB em `100%`, `bCharging` pode estar em `0x00` quando o circuito de carga já encerrou o carregamento. Portanto, a máquina de estados do monitor deve reconhecer carga completa tanto em:
  1. `bCharging == true && percent == 100`, quanto
  2. transição de `Charging` (`bCharging == true` ou carga em progresso via USB) para `percent == 100 && bCharging == false` (corte automático do carregador ao atingir 100%).
- **Desconhecido até teste de descarga:** Não observamos em tempo real o valor exato de `inBuf[7]` (`bCharging`) durante uma carga abaixo de 100% (ex.: `60%`) — assumido `0x01` conforme `0x43be40.c` (`bCharging != 0`) e `0x4472b0.c` (`in_EAX == 1`).
