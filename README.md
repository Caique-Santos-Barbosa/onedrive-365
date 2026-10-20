<div align="center">

<img src="docs/logo.png" width="96" alt="">

# OneDrive 365

**Seus arquivos do OneDrive e do SharePoint na hora, sem esperar a sincronização terminar.**

O OneDrive e o SharePoint aparecem como uma unidade do Windows (`M:`). Tudo é listado na hora e cada arquivo só é baixado quando você abre, como no Google Drive para desktop.

[**⬇ Baixar OneDrive365.exe**](../../releases/latest) · [Como funciona](#-o-que-fizemos-a-arquitetura-do-google-drive-com-o-onedrive) · [Telas](#-telas) · [Para TI](#-para-ti)

</div>

---

## ⏳ Chega de esperar

<img src="docs/screenshots/chega-de-esperar.png" align="right" width="300" alt="OneDrive processando 22.080 alterações">

Quem usa o OneDrive no trabalho conhece essa cena: você clica em **Sincronizar** numa biblioteca do SharePoint, o ícone vira um **"Processando 22.080 alterações"** e você fica esperando.

O cliente oficial da Microsoft cria um *placeholder* local para **cada arquivo** de cada biblioteca sincronizada. Em bibliotecas com milhões de arquivos, isso significa:

- 🐢 **Máquina lenta.** CPU, disco e memória ocupados reconciliando milhares de itens, mesmo sem baixar nenhum conteúdo.
- 📶 **Banda consumida.** O cliente baixa e reprocessa a árvore inteira, o tempo todo.
- ⌛ **Espera para começar.** A pasta só fica utilizável depois que a "sincronização inicial" termina, o que pode levar horas.
- 💥 **Erros de sincronização** com nomes longos, conflitos, a pasta que trava em "processando"...

O **Google Drive para desktop** não tem esse problema. A unidade `G:` aparece na hora, você navega como se tudo estivesse no PC, e o arquivo só é baixado quando você abre.

**O OneDrive 365 leva essa mesma experiência para o OneDrive e o SharePoint.**

<br clear="right">

---

## 🛠 O que fizemos: a arquitetura do Google Drive com o OneDrive

Analisamos como o Google Drive para desktop funciona por dentro (`GoogleDriveFS.exe` + o driver de sistema de arquivos Dokan) e reproduzimos a mesma arquitetura com peças abertas, falando com o Microsoft 365.

| | Google Drive para desktop | OneDrive (Microsoft) | **OneDrive 365** |
|---|---|---|---|
| Como os arquivos aparecem | Unidade virtual `G:` | Placeholder local para cada arquivo | **Unidade virtual `M:`** |
| Driver | Dokan | Cloud Files API | **WinFsp** |
| Motor | `GoogleDriveFS.exe` | `OneDrive.exe` | **`rclone mount` (com patch próprio)** |
| Abrir uma biblioteca grande | Na hora | Espera processar tudo | **Na hora** |
| O que é baixado | Só o que você abre | Metadados de tudo + o que você abre | **Só o que você abre** |
| Mudanças de outras pessoas | Polling | Polling | **Delta do Graph a cada 1 min** |

**Por baixo do capô:**

1. **Unidade sob demanda.** O `rclone mount` com `--vfs-cache-mode full` sobre o WinFsp. A nuvem só é consultada quando você abre uma pasta, e o arquivo só desce quando você o abre. O cache local vai até 20 GB, e o que não é usado há 7 dias sai sozinho.
2. **Uma unidade, dois mundos.** Remotes `combine` + `union` juntam o **Meu OneDrive** e as **bibliotecas do SharePoint** em `M:`, com um único login.
3. **Botão "Sincronizar" do SharePoint.** O app se registra como dono do protocolo `odopen://`, resolve a biblioteca pelo Microsoft Graph e a coloca em `M:\SharePoint` em ~1 segundo, sem processar nada. Outros links `odopen://` continuam indo para o OneDrive original. Também dá para escolher, numa lista só, quais bibliotecas que a conta acessa entram na unidade.
4. **Qualquer conta, qualquer tenant.** Ao trocar de conta ("Desvincular este PC"), bibliotecas que a conta nova não acessa saem sozinhas e o histórico da conta anterior é apagado.
5. **Patch no rclone** (`tools/rclone-patch`). Atributos de pasta para o Windows mostrar ícones próprios, e notificação de mudanças funcionando também na união de remotes.
6. **Backup das pastas do PC, só de envio.** Área de Trabalho, Documentos, Imagens, Downloads, Músicas, Vídeos e pastas extras são copiadas para o Meu OneDrive, com 16 envios em paralelo e envio imediato de cada arquivo alterado. **Nada é baixado para o PC.** Uma ⚙ por pasta permite escolher as subpastas.
7. **Integração com o Windows igual à do OneDrive:**
   - selos de status nos arquivos (extensão de shell em C++);
   - menu do botão direito no Windows 11 e em "Mostrar mais opções" (pacote MSIX esparso), com Compartilhar, Copiar link, Gerenciar acesso, Exibir online e Cor da pasta;
   - ícone na bandeja com animação de sincronização e painel igual ao do OneDrive, com Pausar, Desvincular este PC e Sair;
   - assistente de instalação com as ilustrações do próprio OneDrive.
8. **Instalador único.** Um só `.exe` (~98 MB) traz o .NET, o rclone, o WinFsp, as extensões de shell e a interface. Ele pede o administrador uma única vez.

---

## 📸 Telas

### A unidade M: no Explorador
| OneDrive 365 (M:) | Este Computador |
|---|---|
| ![](docs/screenshots/a5-explorer-unidade-m.png) | ![](docs/screenshots/a6-explorer-este-computador.png) |

### Janela principal
| Início | Início, fazendo backup |
|---|---|
| ![](docs/screenshots/01-home.png) | ![](docs/screenshots/a1-inicio-fazendo-backup.png) |
| **Atividade** | **Atividade, enviando** |
| ![](docs/screenshots/02-activity.png) | ![](docs/screenshots/a2-atividade-enviando.png) |
| **Pastas do computador** (⚙ escolhe subpastas) | **SharePoint** |
| ![](docs/screenshots/04-folders.png) | ![](docs/screenshots/05-sharepoint.png) |
| **Adicionar bibliotecas que eu acesso** (triagem) | **Sobre** |
| ![](docs/screenshots/17-bibliotecas.png) | ![](docs/screenshots/07-about.png) |
| **Notificações** | **Preferências** (horário de sincronização) |
| ![](docs/screenshots/03-notifications.png) | ![](docs/screenshots/06-preferences.png) |

### Bandeja, compartilhamento e cor da pasta
| Painel da bandeja | Compartilhar | Gerenciar acesso | Cor da pasta |
|---|---|---|---|
| ![](docs/screenshots/08-flyout.png) | ![](docs/screenshots/09-compartilhar.png) | ![](docs/screenshots/10-gerenciar-acesso.png) | ![](docs/screenshots/11-cor-da-pasta.png) |

### Assistente de instalação
| Entrar | Unidade M: | Backup das pastas |
|---|---|---|
| ![](docs/screenshots/12-assistente-welcome.png) | ![](docs/screenshots/13-assistente-drive.png) | ![](docs/screenshots/14-assistente-folders.png) |
| **Escolher subpastas** | **Tudo pronto** (← para voltar) | |
| ![](docs/screenshots/15-assistente-pick.png) | ![](docs/screenshots/16-assistente-done.png) | |

<sub>Informações sensíveis (caminhos, nomes de pastas, e-mail, endereços de sites) foram tarjadas.</sub>

---

## 🚀 Como usar

1. Baixe o [**OneDrive365.exe**](../../releases/latest) e abra.
2. O Windows mostra o **SmartScreen** porque o exe ainda não é assinado. Clique em **Mais informações → Executar assim mesmo**.
3. Aceite o pedido de administrador. Ele aparece **uma única vez**, para o driver WinFsp e o menu do botão direito.
4. No assistente:
   - digite o seu e-mail do trabalho e entre na conta Microsoft pelo navegador;
   - escolha as pastas do PC que vão para o backup.
5. Pronto: a unidade **M:** aparece em "Este Computador".
6. No SharePoint, clique em **Sincronizar** em qualquer biblioteca e ela aparece em `M:\SharePoint` na hora.

No dia a dia:
- O app inicia com o Windows. O ícone perto do relógio mostra o status, e um clique abre o painel.
- Você pode pausar a qualquer momento ou definir um horário de sincronização, para não pesar na internet durante o expediente.
- Sem internet, abrem os arquivos que já estão no cache.
- Para desinstalar: Configurações → Aplicativos → OneDrive 365. Nada é apagado na nuvem.

---

## 🏢 Para TI

- A instalação é por usuário, em `%LOCALAPPDATA%\OneDrive 365`. Só o WinFsp e o registro do menu pedem administrador.
- `OneDrive365.exe --add-all` adiciona todas as bibliotecas que o usuário acessa.
- Os logs ficam em `%LOCALAPPDATA%\OneDrive 365\logs` (`app.log`, `mount.log`, `backup.log`).
- O login usa o app OAuth público do rclone. Se o tenant bloqueia o consentimento de usuários, aprove o app "rclone" em **Entra ID → Aplicativos empresariais**.
- O token fica só na máquina do usuário (`rclone.conf`), nunca dentro do exe.

## 🧱 Build

```powershell
pwsh .\build.ps1   # gera dist\OneDrive365.exe
```

Requisitos:
- .NET 8 SDK
- Go 1.26+
- Git
- Visual Studio Build Tools (C++)
- Windows SDK

O build compila o rclone com o patch, baixa o WinFsp, compila as extensões de shell e empacota tudo num único exe.

```
src/            app WinForms + WebView2 (src/ui/*.html)
shell/          selos de status e menu do Explorer (C++)
tools/          patch do rclone, pacote MSIX do menu, geração de ícones
docs/           prints
```

---

## 🗺 Próximos passos

- Assinatura de código, para acabar com o aviso do SmartScreen.
- App OAuth próprio, para tenants que bloqueiam apps de terceiros.

## 📄 Licenças

- [rclone](https://github.com/rclone/rclone): MIT.
- [WinFsp](https://github.com/winfsp/winfsp): GPLv3 com exceção FLOSS. O instalador oficial é redistribuído sem modificação.

<div align="center"><sub>Feito por Caique Santos Barbosa. Não é um produto da Microsoft. OneDrive, SharePoint e Microsoft 365 são marcas da Microsoft.</sub></div>
