// What's new, newest first; shown on /news and /da/nyheder. The weekly changelog workflow
// (.github/workflows/changelog.yml) adds an entry at the top and reads the first `date:` here as "last entry".
// Voice: docs/branding.md. Wrap commands in backticks; they render as code.

export interface ChangelogCopy {
  title: string;
  items: string[];
}

export interface ChangelogEntry {
  /** YYYY-MM-DD, the day the entry was written. */
  date: string;
  en: ChangelogCopy;
  da: ChangelogCopy;
}

export const changelog: ChangelogEntry[] = [
  {
    date: '2026-11-07',
    en: {
      title: 'Connect your agents in two commands',
      items: [
        'Install the CLI with one command, then run `kupro install mcp` to connect Claude Code, Claude Desktop, Cursor, VS Code or Codex.',
        'Settings show how to connect an agent, step by step.',
        'Agents get fewer MCP tools to choose from and shorter answers, so they use less of their context.',
        'Agents and the CLI can read many issues and their activity in one call, and `kupro issue list` takes `--fields` to return only what you need.',
        'Ask the API, the CLI or an agent for a summary of the delivery plan: monthly totals instead of every card.',
      ],
    },
    da: {
      title: 'Forbind dine agenter med to kommandoer',
      items: [
        'Installer CLI’en med én kommando, og kør `kupro install mcp` for at forbinde Claude Code, Claude Desktop, Cursor, VS Code eller Codex.',
        'Under Indstillinger kan du se, trin for trin, hvordan du forbinder en agent.',
        'Agenter får færre MCP-værktøjer at vælge imellem og kortere svar, så de bruger mindre af deres kontekst.',
        'Agenter og CLI’en kan læse mange opgaver og deres aktivitet på én gang, og `kupro issue list` tager `--fields`, så du kun får det, du skal bruge.',
        'Bed API’et, CLI’en eller en agent om en oversigt over leveranceplanen: tal pr. måned i stedet for hvert kort.',
      ],
    },
  },
  {
    date: '2026-10-21',
    en: {
      title: 'The meeting agent joins your standup',
      items: [
        'Start the meeting agent from the meeting panel. It listens and suggests the issue changes people mention, for you to approve.',
        'Share a browser tab or screen with Teams, Meet or Zoom, and the meeting agent hears the call, not just the room.',
        'When two microphones hear the same sentence, you get one proposal, not two.',
        'By default, the transcript is deleted when the meeting ends.',
        'Owners can export a whole workspace as a ZIP file.',
        'Owners can give any person billing access, and each seat includes up to 5 free guests.',
      ],
    },
    da: {
      title: 'Mødeagenten er med til standup',
      items: [
        'Start mødeagenten fra mødepanelet. Den lytter med og foreslår de ændringer til opgaver, folk nævner, så du kan godkende dem.',
        'Del en fane eller din skærm med Teams, Meet eller Zoom, så hører mødeagenten opkaldet og ikke kun lokalet.',
        'Når to mikrofoner hører den samme sætning, får du ét forslag og ikke to.',
        'Som standard slettes transskriptionen, når mødet slutter.',
        'Ejere kan eksportere et helt arbejdsområde som en ZIP-fil.',
        'Ejere kan give alle personer adgang til fakturering, og hver plads giver op til 5 gratis gæster.',
      ],
    },
  },
  {
    date: '2026-10-11',
    en: {
      title: 'The first version of Kupro',
      items: [
        'Create epics, features and tasks with labels and relations, and work on them as a list or a board.',
        'Plan sprints, and see every project’s epics on the delivery plan with milestones and capacity.',
        'Ctrl K finds any issue and runs any command.',
        'Comment, attach files and follow each issue’s activity. Changes show up for everyone without reloading.',
        'Everything you can do in the web app, you can do with the `kupro` CLI or over MCP.',
        'Sign in, create a workspace, invite your team and give agents their own tokens.',
      ],
    },
    da: {
      title: 'Den første udgave af Kupro',
      items: [
        'Opret epics, features og tasks med labels og relationer, og arbejd med dem som liste eller board.',
        'Planlæg sprints, og se alle projekters epics på leveranceplanen med milepæle og kapacitet.',
        'Ctrl K finder enhver opgave og kører enhver kommando.',
        'Skriv kommentarer, vedhæft filer, og følg aktiviteten på hver opgave. Ændringer dukker op hos alle uden genindlæsning.',
        'Alt, hvad du kan i webappen, kan du også med `kupro`-CLI’en eller via MCP.',
        'Log ind, opret et arbejdsområde, inviter dit team, og giv agenter deres egne tokens.',
      ],
    },
  },
];
