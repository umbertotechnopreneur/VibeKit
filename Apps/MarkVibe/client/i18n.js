/* VBWR B
 *
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 *
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 *
 * Modified with AI: OpenAI Codex; readability revision; 2026-10-11
 * Human guidance: Umberto Giacobbi; repository readability rules and MIT licensing
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 *
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 *
 * VBWR E */

let language = "en";
let nativeLanguage = "";
let catalog;
let catalogPromise;
let languageRevision = 0;

/** Remember the native host language before browser locale negotiation. */
export function setSystemLanguage(value) {
  nativeLanguage = value || "";
}

/** Order explicit or host and browser language preferences before loading translations. */
function languageCandidates(preference) {
  return (preference === "system"
    ? [nativeLanguage, ...(navigator.languages || []), navigator.language]
    : [preference]).filter(Boolean);
}

/** Resolve supported locales, keeping Cantonese distinct from Mandarin. */
function resolveLanguage(candidates) {
  for (const candidate of candidates) {
    const locale = candidate.toLowerCase();
    if (locale.startsWith("yue") || ["zh-hk", "zh-mo"].includes(locale)) {
      return "yue-HK";
    }
    if (locale.startsWith("zh")) {
      return "zh-CN";
    }

    const neutralLanguage = locale.split("-")[0];
    if (neutralLanguage === "en" || catalog?.languages.includes(neutralLanguage)) {
      return neutralLanguage;
    }
  }
  return "en";
}

/** Load translations only for a non-English candidate and apply only the latest language request. */
export async function setLanguage(preference = "system") {
  const revision = ++languageRevision;
  const candidates = languageCandidates(preference);
  if (candidates.length && candidates[0].toLowerCase().split("-")[0] !== "en" && !catalog) {
    catalogPromise ??= import("../src/MarkVibe.Core/Localization/strings.json").then(module => module.default);
    catalog = await catalogPromise;
  }
  if (revision !== languageRevision) {
    return language;
  }
  const previousLanguage = language;
  language = resolveLanguage(candidates);
  document.documentElement.lang = language;
  if (previousLanguage !== language) {
    window.dispatchEvent(new CustomEvent("markvibe-language-changed"));
  }
  return language;
}

/** Translate catalog aliases and numbered placeholders, retaining missing keys. */
export function t(key, ...values) {
  const originalKey = key;
  key = catalog?.aliases?.[key] || key;
  const languageIndex = catalog?.languages.indexOf(language) ?? -1;
  const text = languageIndex >= 0
    ? catalog.strings[key]?.[languageIndex] ?? originalKey
    : originalKey;
  return text.replace(/\{(\d+)\}/gu, (match, position) => position < values.length ? String(values[position]) : match);
}

/** Store translation inputs so an element can be refreshed after language changes. */
export function localize(element, key, attribute = "textContent", ...values) {
  element._mvTranslations ??= {};
  element._mvTranslations[attribute] = { key, values };
  if (attribute === "textContent") {
    element.textContent = t(key, ...values);
  } else {
    element.setAttribute(attribute, t(key, ...values));
  }
  return element;
}

/** Refresh registered text, accessible attributes and icon captions under a host. */
export function refreshTranslations(root) {
  for (const element of [root, ...root.querySelectorAll("*")]) {
    for (const [attribute, translation] of Object.entries(element._mvTranslations || {})) {
      localize(element, translation.key, attribute, ...translation.values);
    }
    if (element._mvCaptionKey !== undefined) {
      element._mvRefreshCaption?.();
    }
  }
}
