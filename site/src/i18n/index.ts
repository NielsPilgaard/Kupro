import { da } from './da';
import { en, type Copy } from './en';

export type Locale = 'en' | 'da';
export type Page = 'home' | 'pricing' | 'news' | 'privacy' | 'terms' | 'dpa' | 'subprocessors' | 'cookies' | 'switchJira' | 'switchAzureDevOps' | 'switchLinear' | 'switchTrello' | 'switchGithub' | 'switchMonday' | 'switchYoutrack';

const copy: Record<Locale, Copy> = { en, da };

/** Path of each page per language. Danish slugs are Danish words; the English site has no prefix (ADR-017). */
const paths: Record<Locale, Record<Page, string>> = {
  en: {
    home: '/',
    pricing: '/pricing',
    news: '/news',
    privacy: '/privacy',
    terms: '/terms',
    dpa: '/dpa',
    subprocessors: '/subprocessors',
    cookies: '/cookies',
    switchJira: '/switch/jira',
    switchAzureDevOps: '/switch/azure-devops',
    switchLinear: '/switch/linear',
    switchTrello: '/switch/trello',
    switchGithub: '/switch/github',
    switchMonday: '/switch/monday',
    switchYoutrack: '/switch/youtrack',
  },
  da: {
    home: '/da',
    pricing: '/da/priser',
    news: '/da/nyheder',
    privacy: '/da/privatliv',
    terms: '/da/vilkaar',
    dpa: '/da/databehandleraftale',
    subprocessors: '/da/underdatabehandlere',
    cookies: '/da/cookies',
    switchJira: '/da/skift/jira',
    switchAzureDevOps: '/da/skift/azure-devops',
    switchLinear: '/da/skift/linear',
    switchTrello: '/da/skift/trello',
    switchGithub: '/da/skift/github',
    switchMonday: '/da/skift/monday',
    switchYoutrack: '/da/skift/youtrack',
  },
};

export const pages = Object.keys(paths.en) as Page[];
export const t = (locale: Locale): Copy => copy[locale];
export const path = (locale: Locale, page: Page): string => paths[locale][page];
export const other = (locale: Locale): Locale => (locale === 'en' ? 'da' : 'en');

/** Where "Sign in" and "Start a free trial" go: the app (app.<domain> in production). */
export const appUrl: string = import.meta.env.PUBLIC_APP_URL ?? 'http://localhost:5180';
