# HelpMe — terminal help with `hm`

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows · Linux](https://img.shields.io/badge/platform-Windows_%C2%B7_Linux-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](LICENSE)

I use Git every day, and I still forget its commands. Other command-line tools give me more syntax to remember. I built HelpMe so I can describe what I need, read an explanation, and review suggested commands in the same terminal.

```powershell
hm "How do I undo my last local commit without losing my changes?"
```

I maintain HelpMe with help from a few contributors. It is a .NET 10 console app in pre-production. AI features use your own OpenAI API account; API charges apply.

## Built with care

I want terminal help to stay useful without wasting your computer's resources. I review third-party dependencies and their sources, pay attention to memory and performance, and keep you in control of commands. Checks and protections depend on the feature; they are not a security guarantee.

## Get started

Install the .NET 10 SDK, Git, and PowerShell 7, then run from source:

```powershell
git clone https://github.com/umbertotechnopreneur/VibeKit.git
cd VibeKit/Apps/HelpMe
dotnet run --project HelpMe/HelpMe.csproj -- --setup
dotnet run --project HelpMe/HelpMe.csproj -- "How do I list the largest files here?"
```

Setup covers language, model, answer style, and command checks. The interface supports English, Italian, French, German, Spanish, and Vietnamese. No special font is required; use `--no-emoji` or `--no-animation` for simpler output.

Enter your API key in setup or use your secret manager. Never put it in a command. On Windows, setup can save it for your user account. It works in the current session; fully close and reopen your terminal app and any IDE hosting it before the next session. On Linux, a key entered in setup lasts for that session only.

The examples below use `hm`. From source, replace it with `dotnet run --project HelpMe/HelpMe.csproj --`.

## Platforms and downloads

HelpMe targets Windows and Linux on x64 and ARM64. Linux packaging uses `.deb` files with the .NET runtime included; native libraries are declared as dependencies. See [build, installation, and release instructions](scripts/README.md). A configured workflow does not mean a package has already been published. I do not provide commercial Linux support or promise compatibility with every distribution.

The commercial edition targets Windows 11 on x64 and ARM64 and uses MSIX. The existing [Microsoft Store listing](https://apps.microsoft.com/detail/9P2WJBVQMW4J) is for the previous PromptMeUp release; the HelpMe rebrand has not been submitted.

## Everyday use

| What you need | Command |
| --- | --- |
| Ask a question | `hm "your question"` |
| Continue a conversation | `hm --chat` |
| Change settings | `hm --setup` |
| Save a note | `hm --remember "Prefer short explanations."` |
| Read or edit saved notes | `hm --memories` |
| Forget a note | `hm --forget "id or description"` |
| Diagnose a log | `hm --diagnose --file build.log` |
| Draft a script | `hm --script "your request"` |
| Work through a plan | `hm --plan "your goal"` |
| See commands and options | `hm --help` |

Saved notes are global across chats and folders. Skills and memory maintenance start disabled; `hm --skills` and `hm --learning` let you manage them. Nothing keeps running in the background when you close HelpMe.

In chat, `/status` or `/context` shows conversation usage. The session summary also appears at exit or when operating context reaches 80%. Use `/clear` to start a fresh conversation while keeping saved notes and usage totals.

See the [command reference](docs/CLI_REFERENCE.md), [skills and memory guide](docs/SKILLS_AND_MEMORY.md), and [costs and context guide](docs/OPENAI_COSTS_AND_CACHING.md) for details.

## Before a command runs

Direct execution is enabled by default for questions and chat. HelpMe shows the exact command, checks its risk locally and with AI, then starts a five-second countdown. Press Enter to run now, or Esc or Ctrl+C to cancel. High or critical risk and failed AI reviews block execution. Output goes back to the AI for analysis.

For manual confirmation, turn off **Direct execution (5-second countdown)** in the **AI** section of `hm --setup`. `--direct` enables it for the current session. See the [direct-mode reference](docs/CLI_REFERENCE.md#use-direct-execution).

Choose **Command shell** in `hm --setup` under **Commands**: Auto, PowerShell, or Bash. Auto detects the calling shell on Linux; Windows defaults to PowerShell. Override the saved choice for one invocation with `hm --shell bash "your question"` or `hm --shell pwsh "your question"`; explicit `--shell auto` requests detection for that invocation. Ambiguous Linux detection requires an explicit choice. Each command preview names the interpreter.

Bash commands start without shell profiles or imported shell functions. Only a small set of literal inspection commands is eligible for direct execution; other Bash commands require manual confirmation. Script execution, syntax validation, file previews, and skill actions still use explicit PowerShell wrappers, so those workflows require PowerShell 7. Saved plans retain their original shell; older plans without shell metadata must be recreated.

Commands run with your account's permissions, cancellation, and bounded captured output. They can change your real files and system settings.

Settings, notes, logs, and request history stay on your machine. OpenAI receives your question, recent messages, selected notes, terminal context, and command output used for follow-up. HelpMe removes recognizable secrets, but cannot catch every private detail. Read the [privacy guide](docs/PRIVACY.md) before sharing sensitive information.

## Screenshots

[![PromptMeUp reviewing a Git command in Italian](docs/assets/promptmeup-command-it-framed-v1.png)](docs/assets/promptmeup-command-it-framed-v1.png)

I captured this screenshot before the HelpMe rebrand, cropped and framed it, and hid my personal path. The app text and numbers have not been redrawn. Other retained illustrations and screenshots are described in the [artwork credits](docs/assets/README.md).

## Get involved

Found a rough edge? Have a small improvement in mind? [Share an idea or report a bug](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BHelpMe%5D%20), or read the [contribution guide](CONTRIBUTING.md).

> [!TIP]
> **Know your way around a Mac terminal?**
>
> HelpMe targets Windows and Linux. I do not own a Mac, so **macOS is not supported or tested here**. A contributor who can maintain the shell integration, setup and packaging would make a Mac port possible to explore.
>
> [Tell me what you can help with](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BHelpMe%5D%20). Command previews and cancellation matter just as much as getting the app to launch.

Documentation and contributions are in English; the app supports all six interface languages.

| For users | For contributors |
| --- | --- |
| [Command guide](docs/CLI_REFERENCE.md) | [How the code is organized](docs/ARCHITECTURE.md) |
| [Privacy](docs/PRIVACY.md) | [Build and testing guide](CONTRIBUTING.md#get-a-local-build-running) |
| [Costs and conversation memory](docs/OPENAI_COSTS_AND_CACHING.md) | [Build and release process](scripts/README.md) |
| [Support](SUPPORT.md) | [How the project is run](GOVERNANCE.md) |

Security issue? [Report it privately](SECURITY.md), following the [security policy](SECURITY.md).

## License and credits

HelpMe is released under the **[MIT License](LICENSE)**. Use it, adapt it, and build on it while keeping the copyright and license notice.

I built HelpMe with help from contributors, and I’m grateful to the people behind the libraries I use. See the [library credits](THIRD_PARTY_NOTICES.md), [license texts](LICENSES/README.md), and [artwork credits](docs/assets/README.md).

## More from MeUp

<p align="center">
  <img src="docs/assets/meup/promptmeup-mark-v1.png" alt="HelpMe brand mark: a terminal prompt and guiding spark" width="132" />
</p>

<p align="center">
  <a href="https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/AgentInbox"><strong>AgentInbox</strong></a> · Connect your inboxes to your AI assistant.<br />
  <a href="https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/HelpMe"><strong>HelpMe</strong></a> · Can't remember that command? Git, Bash, or PowerShell: describe what you want to do. hm is here to help.<br />
  <a href="https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/WorkTrail"><strong>WorkTrail</strong></a> · Compare time zones and availability, with independent desktop widgets.
</p>

<p align="center"><sub>Brand mark, not an app icon. <a href="docs/assets/meup/README.md">Visual style and image credits</a>.</sub></p>

<p align="center">I’m Umberto Giacobbi. I built and maintain HelpMe with help from contributors.<br />Find me on <a href="https://www.linkedin.com/in/umbertogiacobbi/">LinkedIn</a> or at <a href="https://umbertogiacobbi.biz/">umbertogiacobbi.biz</a>.</p>

## A note on security

I care about what goes into this project. Before I add a third-party library or prebuilt binary, including files supplied without source code, I scan the downloaded package, its extracted files and any dependencies with Microsoft Windows Defender. I keep new imports aside until the scan finishes without detections.

I keep a record of where each new import came from and the scan result. If a scan cannot finish or flags something, I leave it out.

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created HelpMe with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[HelpMe on GitHub](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/HelpMe) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore, adapt and reuse the open-source VibeWare materials under the project license — my contribution to the developer community.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="https://raw.githubusercontent.com/umbertotechnopreneur/VibeWare/main/Branding/vibeware-logo.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>
