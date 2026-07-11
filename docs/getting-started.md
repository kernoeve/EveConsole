# Getting Started

## Requirements

- Windows 10 or 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (only needed if you build from source)

> Linux builds are planned for the 1.0 release. Today the app is built and tested on Windows (`net9.0-windows`).

## Install & run

Until packaged releases are published, run from source:

```powershell
git clone https://github.com/kernoeve/EveCortex.git
cd EveCortex
dotnet restore
dotnet run
```

## First launch

1. When the app opens, click **Log in with Eve**.
2. You'll be taken to EVE's official Single Sign-On (SSO) page in your browser. Log in and authorize the character.
3. Repeat for any additional characters you want tracked.

Your data is stored locally at:

```
%LOCALAPPDATA%\EveCortex\EveCortex.db
```

Nothing is uploaded anywhere — the app talks only to CCP's ESI API to refresh your data.

## How data stays fresh

While Eve Cortex is running it performs background ESI pulls on a schedule. Most screens update automatically as new data arrives, so you can leave it open in the background. Some data (like market price history) is cached and refreshed on longer intervals to respect ESI limits.

## Recommended next steps

Most tools depend on a little configuration first:

1. **[Configure a market](configuring-markets.md)** so the app knows where prices come from.
2. **[Set up an industry park](industry-parks.md)** so build costs and the Production Calculator are accurate.
3. Optionally, **[set up the AI agent](ai-agent-eden.md)**.

<!--
  SCREENSHOT SLOTS (add files to docs/images/, then uncomment):

  Login screen:
  ![Log in with Eve](images/login.png)

  Main overview after login:
  ![Overview](images/overview.png)
-->
