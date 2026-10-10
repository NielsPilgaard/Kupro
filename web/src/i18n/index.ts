import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import da from './locales/da.json';
import en from './locales/en.json';

export const supportedLanguages = ['en', 'da'] as const;
export type Language = (typeof supportedLanguages)[number];

// English is the source language; Danish must have every key (ADR-011, checked by `bun run i18n:check`).
export const resources = { en: { translation: en }, da: { translation: da } } as const;

function detectLanguage(): Language {
  // TODO(task 003): prefer the locale stored on the user profile once it exists.
  return navigator.language.toLowerCase().startsWith('da') ? 'da' : 'en';
}

void i18n.use(initReactI18next).init({
  resources,
  lng: detectLanguage(),
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
});

i18n.on('languageChanged', (lng) => document.documentElement.setAttribute('lang', lng));
document.documentElement.setAttribute('lang', i18n.language);

export default i18n;
