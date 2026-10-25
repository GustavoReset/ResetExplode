# RESET EXPLODE — port para Linux (Ubuntu 26.10)

Fork do [ResetExplode](https://github.com/GustavoReset/ResetExplode) (WPF/Windows) adaptado para Linux com **Avalonia UI** e **.NET 10**.
O código original do Windows está preservado em [windows-legacy/](windows-legacy/) (licença [MIT](LICENSE), copyright do autor original).

## O que já funciona
| Aba | Fonte de dados no Linux |
|---|---|
| Sistema | `/etc/os-release`, `/sys/class/dmi`, `/proc/cpuinfo`, `lscpu`, `/proc/meminfo`, `dmidecode` (root), `lsblk` |
| Placa de vídeo | `lspci`, sysfs (PCIe, VRAM, carga AMD), `nvidia-smi` |
| Sensores | hwmon (temperaturas, ventoinhas, tensões), cpufreq, `/proc/stat`, RAPL, `nvidia-smi` |
| Stress | CPU em todos os threads e/ou RAM, limite de temperatura e duração |
| Discos | `lsblk` + S.M.A.R.T. via `smartctl` (`sudo apt install smartmontools`) |
| Rede | latência/jitter/perda, DNS, download/upload, bufferbloat |
| Energia | `/sys/class/power_supply` (bateria, desgaste, fonte) |
| Jogos (Steam) | `libraryfolders.vdf` / `appmanifest_*.acf`; inicia via `steam://` |
| Ferramentas | autostart XDG (`~/.config/autostart`), portas seriais (`/dev/ttyUSB*`), ajustes de desempenho (leitura) |
| Relatório | `.txt` em `~` ou `resetexplode --report` no terminal |

Para dados completos (RAM por pente, S.M.A.R.T., RAPL) execute com `sudo`.

## Fora do port (exclusivos do Windows)
Drivers `.inf/.sys` (CH340/CP210x/FTDI já vêm no kernel), Registro, winget, DPAPI, iCUE, Direct3D (stress/bench de GPU),
overlay sobre jogos. Stress/bench de GPU via Vulkan/OpenGL é um próximo passo possível.

## Compilar e executar
```bash
sudo apt install dotnet-sdk-10.0 pciutils smartmontools
dotnet run -c Release
./publicar.sh   # executável único em publish/
```
No Wayland (padrão do Ubuntu) o Avalonia roda via XWayland/Wayland nativo; janelas "sempre no topo" têm limitações.
