# Drivers seriais incluídos (RESET MAKER)

Drivers oficiais, sem modificação, embutidos no `ResetExplode.exe` e instalados pela aba
**RESET MAKER › Drivers seriais › Drivers incluídos** (via `pnputil /add-driver /install`).
Cada pasta é um pacote `.inf` + `.cat` (+ `.sys`/`.dll`) com a assinatura original do fabricante.

**Estes arquivos não fazem parte da licença MIT do RESET EXPLODE:** pertencem aos respectivos fabricantes
e são distribuídos conforme os termos de cada um.

| Pasta | Chip | Versão | Origem oficial | Assinatura |
|---|---|---|---|---|
| `CH340` | WCH CH340/CH341 | 4.0.2026.02 (11/02/2026) | [wch-ic.com · CH341SER.ZIP](https://www.wch-ic.com/downloads/CH341SER_ZIP.html), pasta `WIN 1X` (Windows 10/11) | Microsoft Windows Hardware Compatibility Publisher (WHQL) |
| `CP210x` | Silicon Labs CP2102/CP2102N/CP2104/CP210x | 11.6.0.420 (14/08/2026) | [silabs.com · CP210x Universal Windows Driver](https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers) (Windows 10 1803+ e 11) | Microsoft (WHQL) no catálogo; `.sys` Silicon Laboratories |
| `FTDI` | FTDI FT232R/FT231X/FT2232... | 2.12.36.20 (28/10/2024) | [ftdichip.com · VCP Drivers](https://ftdichip.com/drivers/vcp-drivers/), CDM WHQL Certified (zip) | Microsoft (WHQL) |
| `Arduino` | Placas Arduino/Genuino (VID 2341, 2A03) | arduino 1.2.3.0, genuino 1.0.3.0, arduino-org 1.1.1.0, gemma 1.0.0.0 | [github.com/arduino/Arduino · build/windows/dist/drivers](https://github.com/arduino/Arduino/tree/master/build/windows/dist/drivers) | Arduino LLC / Arduino srl |

SHA-256 dos arquivos baixados:
- `CH341SER.ZIP` 59967d9ce371d0bf3df02dec0b66c8dfbf9ca576da0572ff4404148a7c381807
- `CP210x_Universal_Windows_Driver.zip` 6d9259475219c8c6cc19512053e96d435ebbada3d90457fba7d49ee8d524ba98
- `CDM-v2.12.36.20-WHQL-Certified.zip` e8ebf401d06994183694d78e5855ed5ab026bdfa8c3d4e4ccde09ec238a0eaf1

Do pacote CP210x ficaram só o `.inf`, o `.cat` e os `.sys` por arquitetura; do FTDI, só `.inf`, `.cat`, `.sys` e `.dll`
(sem os `.lib`/`.h` de desenvolvimento). Nenhum arquivo foi alterado, então as assinaturas continuam válidas.

Para incluir outro chip (ex.: Prolific): crie a pasta com os arquivos do pacote oficial (sem o instalador `.exe`)
e confira se `MakerDrivers.Chips` aponta para ela no campo `Bundle`. O `.csproj` embute tudo em `Drivers\` automaticamente.
