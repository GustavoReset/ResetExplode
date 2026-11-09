/*
    Regras YARA próprias do Fort Knox (MURALHA, Fase 3.5b). Escritas aqui, sem copiar conjuntos de terceiros.
    Cada regra declara em meta:
      veredito  = "Suspeito" ou "Malicioso"
      descricao = o motivo em linguagem clara (aparece para a pessoa)
      extensoes = opcional, tipos de arquivo em que a regra vale (".ps1,.bat")
    Estas regras vão comprimidas dentro do app (ver FortKnox.csproj), então o texto delas não faz o próprio
    Fort Knox ser acusado. Arquivos com assinatura digital válida nem chegam aqui.
    Lembrete: regra de executável deve exigir dois ou mais sinais; um sozinho acusa programa legítimo.
*/

import "pe"

rule FK_Eicar
{
    meta:
        veredito = "Malicioso"
        descricao = "Arquivo de teste EICAR (inofensivo)"
    strings:
        $eicar = "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!"
    condition:
        $eicar at 28 and filesize < 130
}

rule FK_Injecao_Em_Outro_Processo
{
    meta:
        veredito = "Suspeito"
        descricao = "Executável preparado para injetar código em outros programas"
    condition:
        uint16(0) == 0x5A4D and
        pe.imports("kernel32.dll", "OpenProcess") and
        pe.imports("kernel32.dll", "VirtualAllocEx") and
        pe.imports("kernel32.dll", "WriteProcessMemory") and
        (pe.imports("kernel32.dll", "CreateRemoteThread") or pe.imports("ntdll.dll", "NtCreateThreadEx") or pe.imports("ntdll.dll", "RtlCreateUserThread"))
}

rule FK_Captura_De_Teclado_Com_Envio
{
    meta:
        veredito = "Suspeito"
        descricao = "Executável que registra o teclado e fala com a internet (padrão de keylogger)"
    condition:
        uint16(0) == 0x5A4D and
        (pe.imports("user32.dll", "SetWindowsHookExA") or pe.imports("user32.dll", "SetWindowsHookExW") or pe.imports("user32.dll", "GetAsyncKeyState")) and
        (pe.imports("wininet.dll", "InternetOpenA") or pe.imports("wininet.dll", "InternetOpenW") or
         pe.imports("winhttp.dll", "WinHttpOpen") or pe.imports("ws2_32.dll", "send") or pe.imports("ws2_32.dll", 19)) and
        (pe.imports("user32.dll", "GetForegroundWindow") and (pe.imports("user32.dll", "GetWindowTextA") or pe.imports("user32.dll", "GetWindowTextW")))
}

rule FK_Ransomware_Apaga_Copias
{
    meta:
        veredito = "Suspeito"
        descricao = "Apaga as cópias de sombra e desliga a recuperação do Windows (comum em ransomware)"
    strings:
        $vss1 = "vssadmin" ascii wide nocase
        $vss2 = "delete shadows" ascii wide nocase
        $wmi  = "shadowcopy delete" ascii wide nocase
        $bcd1 = "recoveryenabled no" ascii wide nocase
        $bcd2 = "bootstatuspolicy ignoreallfailures" ascii wide nocase
        $wbad = "wbadmin delete catalog" ascii wide nocase
    condition:
        (($vss1 and $vss2) or $wmi) and ($bcd1 or $bcd2 or $wbad)
}

rule FK_Ransomware_Nota_De_Resgate
{
    meta:
        veredito = "Suspeito"
        descricao = "Executável com texto de pedido de resgate e de criptografia de arquivos"
    strings:
        $r1 = "your files have been encrypted" ascii wide nocase
        $r2 = "your files are encrypted" ascii wide nocase
        $r3 = "seus arquivos foram criptografados" ascii wide nocase
        $p1 = "bitcoin" ascii wide nocase
        $p2 = "monero" ascii wide nocase
        $p3 = ".onion" ascii wide nocase
        $p4 = "decrypt" ascii wide nocase
    condition:
        uint16(0) == 0x5A4D and any of ($r*) and 2 of ($p*)
}

rule FK_Ladrao_De_Senhas_Do_Navegador
{
    meta:
        veredito = "Suspeito"
        descricao = "Lê as senhas salvas do navegador e envia para um bot de Telegram ou Discord"
    strings:
        $b1 = "\\Login Data" ascii wide nocase
        $b2 = "Local State" ascii wide nocase
        $b3 = "encrypted_key" ascii wide nocase
        $b4 = "logins.json" ascii wide nocase
        $x1 = "api.telegram.org/bot" ascii wide nocase
        $x2 = "discord.com/api/webhooks" ascii wide nocase
        $x3 = "discordapp.com/api/webhooks" ascii wide nocase
    condition:
        2 of ($b*) and any of ($x*)
}

rule FK_PowerShell_Desliga_AMSI
{
    meta:
        veredito = "Suspeito"
        descricao = "Script que desliga a inspeção de scripts do Windows (AMSI) antes de rodar código"
        extensoes = ".ps1,.psm1,.bat,.cmd,.vbs,.vbe,.js,.jse,.wsf,.hta"
    strings:
        $a1 = "AmsiUtils" ascii wide nocase
        $a2 = "amsiInitFailed" ascii wide nocase
        $a3 = "AmsiScanBuffer" ascii wide nocase
        $p1 = "VirtualProtect" ascii wide nocase
        $p2 = "GetProcAddress" ascii wide nocase
    condition:
        ($a1 and $a2) or ($a3 and $p1 and $p2)
}

rule FK_Script_Executa_Pelo_Regsvr32_Ou_Mshta
{
    meta:
        veredito = "Suspeito"
        descricao = "Script que baixa e executa código pela internet usando programas do próprio Windows"
        extensoes = ".bat,.cmd,.ps1,.psm1,.vbs,.vbe,.js,.jse,.wsf,.hta"
    strings:
        $r1 = /regsvr32(\.exe)?\s+[^\r\n]{0,40}\/i:\s*https?:\/\//i
        $r2 = "scrobj.dll" ascii wide nocase
        $m1 = /mshta(\.exe)?\s+["']?https?:\/\//i
        $m2 = /mshta(\.exe)?\s+["']?(vbscript|javascript):/i
        $c1 = /certutil(\.exe)?\s+[^\r\n]{0,40}-urlcache[^\r\n]{0,40}https?:\/\//i
    condition:
        ($r1 and $r2) or $m1 or $m2 or $c1
}

rule FK_Script_Persiste_Escondido
{
    meta:
        veredito = "Suspeito"
        descricao = "Script que se coloca para iniciar com o Windows e roda escondido"
        extensoes = ".bat,.cmd,.ps1,.psm1,.vbs,.vbe,.js,.jse,.wsf,.hta"
    strings:
        $run1 = "CurrentVersion\\Run" ascii wide nocase
        $run2 = "schtasks" ascii wide nocase
        $run3 = "Start Menu\\Programs\\Startup" ascii wide nocase
        $h1 = "-w hidden" ascii wide nocase
        $h2 = "-windowstyle hidden" ascii wide nocase
        $h3 = ".Run(" ascii wide nocase
        $h4 = ", 0, " ascii wide
        $d1 = "DownloadString" ascii wide nocase
        $d2 = "DownloadFile" ascii wide nocase
        $d3 = "Invoke-WebRequest" ascii wide nocase
        $d4 = "MSXML2.XMLHTTP" ascii wide nocase
    condition:
        any of ($run*) and (($h1 or $h2) or ($h3 and $h4)) and any of ($d*)
}
