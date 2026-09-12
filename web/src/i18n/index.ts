import i18next from "i18next";
import { initReactI18next } from "react-i18next";

import en from "./en";
import zh from "./zh";

export const LANG_STORAGE_KEY = "infinityci.lang";
export type AppLanguage = "zh" | "en";

function initialLanguage(): AppLanguage {
  try {
    const stored = localStorage.getItem(LANG_STORAGE_KEY);
    if (stored === "en" || stored === "zh") return stored;
  } catch {
    // storage unavailable — fall back to zh
  }
  return "zh";
}

void i18next.use(initReactI18next).init({
  resources: {
    zh: { translation: zh },
    en: { translation: en },
  },
  lng: initialLanguage(),
  fallbackLng: "zh",
  // React already escapes rendered strings.
  interpolation: { escapeValue: false },
});

/** Switch the UI language and remember the choice across sessions. */
export function changeAppLanguage(lng: AppLanguage) {
  localStorage.setItem(LANG_STORAGE_KEY, lng);
  void i18next.changeLanguage(lng);
}

export default i18next;
