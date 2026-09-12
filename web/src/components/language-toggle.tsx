import { useTranslation } from "react-i18next";

import { changeAppLanguage } from "@/i18n";

/** Segmented 中文 / EN switcher; `dark` matches the top navigation bar. */
export function LanguageToggle({ dark = false }: { dark?: boolean }) {
  const { i18n } = useTranslation();
  const isZh = (i18n.language ?? "zh").startsWith("zh");

  const wrap = dark
    ? "divide-white/25 border-white/25 text-white"
    : "divide-[#d0d7de] border-[#d0d7de] text-[#24292f]";
  const active = dark ? "bg-white/20 font-medium" : "bg-[#eaeef2] font-medium";
  const inactive = dark ? "hover:bg-white/10" : "hover:bg-[#f6f8fa]";

  return (
    <div className={`flex overflow-hidden rounded-md border text-xs ${wrap}`}>
      <button
        type="button"
        onClick={() => changeAppLanguage("zh")}
        className={`px-2 py-1 ${isZh ? active : inactive}`}
      >
        中文
      </button>
      <button
        type="button"
        onClick={() => changeAppLanguage("en")}
        className={`border-l px-2 py-1 first:border-l-0 ${!isZh ? active : inactive} ${
          dark ? "border-white/25" : "border-[#d0d7de]"
        }`}
      >
        EN
      </button>
    </div>
  );
}
