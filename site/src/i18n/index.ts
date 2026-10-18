import { da } from './da';
import { en, type Copy } from './en';

export type Locale = 'en' | 'da';
export type Page = 'home' | 'pricing' | 'privacy' | 'terms' | 'dpa' | 'subprocessors' | 'cookies';

const copy: Record<Locale, Copy> = { en, da };

/** Path of each page per language. Danish slugs are Danish words; the English site has no prefix (ADR-017). */
const paths: Record<Locale, Record<Page, string>> = {
  en: {
    home: '/',
    pricing: '/pricing',
    privacy: '/privacy',
    terms: '/terms',
    dpa: '/dpa',
    subprocessors: '/subprocessors',
    cookies: '/cookies',
  },
  da: {
    home: '/da',
    pricing: '/da/priser',
    privacy: '/da/privatliv',
    terms: '/da/vilkaar',
    dpa: '/da/databehandleraftale',
    subprocessors: '/da/underdatabehandlere',
    cookies: '/da/cookies',
  },
};

export const t = (locale: Locale): Copy => copy[locale];
export const path = (locale: Locale, page: Page): string => paths[locale][page];
export const other = (locale: Locale): Locale => (locale === 'en' ? 'da' : 'en');

/** Where "Sign in" and "Start a free trial" go: the app (app.<domain> in production). */
export const appUrl: string = import.meta.env.PUBLIC_APP_URL ?? 'http://localhost:5180';
