using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace MdTranslatorViewer.Services;

internal sealed class MarkdownHtmlRenderer
{
    private static readonly Regex LineNumberTagRegex = new(
        @"<(?<tag>h[1-6]|p|li|blockquote|pre|hr|table)\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public DocumentRenderPayload CreateDocumentPayload(
        string markdown,
        string sourcePath,
        string paneTitle,
        MarkdownPipeline pipeline,
        IReadOnlyList<CodeBlockTranslationPayload>? codeBlockTranslations = null)
    {
        return new DocumentRenderPayload
        {
            HtmlBody = RenderMarkdownBody(markdown, pipeline),
            BaseUri = new Uri(AppendDirectorySeparator(Path.GetDirectoryName(sourcePath)!)).AbsoluteUri,
            PaneTitle = paneTitle,
            FileName = Path.GetFileName(sourcePath),
            SourceLineCount = CountSourceLines(markdown),
            SourcePath = sourcePath,
            CodeBlockTranslations = codeBlockTranslations ?? Array.Empty<CodeBlockTranslationPayload>(),
        };
    }

    private static string BuildThemeCssVariables(ViewerDocumentTheme theme)
    {
        return $$"""
      color-scheme: {{theme.ColorScheme}};
      --bg: {{theme.Background}};
      --panel: {{theme.Panel}};
      --ink: {{theme.Ink}};
      --muted: {{theme.Muted}};
      --line: {{theme.Line}};
      --line-soft: {{theme.LineSoft}};
      --accent: {{theme.Accent}};
      --accent-strong: {{theme.AccentStrong}};
      --accent-soft: {{theme.AccentSoft}};
      --accent-underline: {{theme.AccentUnderline}};
      --accent-underline-hover: {{theme.AccentUnderlineHover}};
      --code-bg: {{theme.CodeBackground}};
      --code-ink: {{theme.CodeForeground}};
      --pre-ink: {{theme.PreForeground}};
      --quote-border: {{theme.QuoteBorder}};
      --quote-bg: {{theme.QuoteBackground}};
      --quote-ink: {{theme.QuoteForeground}};
      --heading-1: {{theme.Heading1}};
      --heading-2: {{theme.Heading2}};
      --heading-3: {{theme.Heading3}};
      --line-number: {{theme.LineNumber}};
      --selection: {{theme.Selection}};
      --scrollbar-thumb: {{theme.ScrollbarThumb}};
      --scrollbar-thumb-hover: {{theme.ScrollbarThumbHover}};
      --drop-overlay-bg: {{theme.DropOverlayBackground}};
      --drop-pill-border: {{theme.DropPillBorder}};
      --drop-pill-bg: {{theme.DropPillBackground}};
      --drop-dot: {{theme.DropDot}};
      --drop-title: {{theme.DropTitle}};
      --drop-copy: {{theme.DropCopy}};
""";
    }

    public string RenderDocumentShell(string webMessageToken, ViewerDocumentTheme theme)
    {
        return $$"""
<!doctype html>
<html lang="ko">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <base id="mdv-base" href="about:blank">
  <title>MD Translator Viewer</title>
  <style>
    :root {
{{BuildThemeCssVariables(theme)}}
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      font-family: "Segoe UI", "Malgun Gothic", "Apple SD Gothic Neo", sans-serif;
      background: var(--bg);
      color: var(--ink);
      line-height: 1.78;
      font-size: 16px;
    }

    .page {
      --page-pad-x: 4px;
      --gutter-width: 48px;
      --gutter-gap: 22px;
      max-width: 920px;
      margin: 0 auto;
      padding: 28px var(--page-pad-x) 120px calc(var(--page-pad-x) + var(--gutter-width) + var(--gutter-gap));
      position: relative;
    }

    .pane-title {
      margin: 0 0 30px;
      color: var(--muted);
      font-size: 12px;
      font-weight: 600;
      letter-spacing: 0.01em;
    }

    #mdv-content {
      position: relative;
      z-index: 1;
    }

    .mdv-line-gutter {
      position: absolute;
      top: 0;
      bottom: 0;
      left: var(--page-pad-x);
      width: var(--gutter-width);
      pointer-events: none;
      user-select: none;
      z-index: 0;
    }

    .mdv-line-number {
      position: absolute;
      left: 0;
      width: 100%;
      color: var(--line-number);
      text-align: right;
      font-family: "Cascadia Code", Consolas, monospace;
      font-size: 13px;
      line-height: 1;
      font-variant-numeric: tabular-nums;
      transform: translateY(-50%);
      white-space: nowrap;
    }

    h1, h2, h3, h4, h5, h6 {
      margin: 1.45em 0 0.48em;
      color: var(--ink);
      line-height: 1.24;
      font-weight: 700;
    }

    h1 {
      margin-top: 0.1em;
      font-size: 2.7rem;
      letter-spacing: -0.03em;
      color: var(--heading-1);
      padding-bottom: 0.3em;
      border-bottom: 1px solid var(--line);
    }

    h2 {
      font-size: 1.92rem;
      color: var(--heading-2);
      padding-bottom: 0.25em;
      border-bottom: 1px solid var(--line-soft);
    }
    h3 { font-size: 1.48rem; color: var(--heading-3); }
    h4 { font-size: 1.16rem; color: var(--ink); }

    p, ul, ol, blockquote, table, pre {
      margin: 0 0 1.18em;
    }

    ul, ol {
      padding-left: 1.7em;
    }

    li {
      margin: 0.3em 0;
    }

    a {
      color: var(--accent);
      text-decoration: underline;
      text-decoration-color: var(--accent-underline);
      text-underline-offset: 0.13em;
    }

    a:hover {
      color: var(--accent);
      text-decoration-color: var(--accent-underline-hover);
    }

    img {
      max-width: 100%;
      border-radius: 10px;
      border: 1px solid var(--line);
    }

    code {
      padding: 0.16em 0.42em;
      border-radius: 5px;
      background: var(--code-bg);
      border: 1px solid var(--line);
      color: var(--code-ink);
      font-family: "Cascadia Code", Consolas, monospace;
      font-size: 0.88em;
    }

    pre {
      overflow: auto;
      padding: 18px 20px;
      border: 1px solid var(--line-soft);
      border-radius: 10px;
      background: var(--panel);
      position: relative;
    }

    pre code {
      display: block;
      padding: 0;
      background: transparent;
      border: none;
      color: var(--pre-ink);
      white-space: normal;
    }

    .mdv-code-line {
      display: block;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      word-break: break-word;
    }

    pre.mdv-code-block {
      padding-right: 20px;
    }

    .mdv-code-translate-toggle {
      position: absolute;
      top: 12px;
      right: 12px;
      width: 40px;
      height: 34px;
      padding: 0;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border: 1px solid var(--line);
      border-radius: 10px;
      background: rgba(255, 255, 255, 0.04);
      color: var(--muted);
      font: inherit;
      cursor: pointer;
      transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
    }

    .mdv-code-translate-toggle:hover:not(:disabled) {
      background: rgba(255, 255, 255, 0.08);
      color: var(--ink);
      border-color: var(--line-soft);
    }

    .mdv-code-translate-toggle:disabled {
      cursor: default;
      opacity: 0.72;
    }

    .mdv-code-translate-toggle.is-on {
      background: color-mix(in srgb, var(--accent-soft) 55%, transparent);
      border-color: color-mix(in srgb, var(--accent) 52%, var(--line));
      color: var(--ink);
    }

    .mdv-code-translate-toggle.is-loading {
      color: var(--ink);
    }

    .mdv-code-translate-icon {
      position: relative;
      width: 18px;
      height: 18px;
      display: block;
    }

    .mdv-code-translate-icon::after {
      content: "";
      position: absolute;
      left: 3px;
      right: 3px;
      top: 8px;
      height: 1.5px;
      border-radius: 999px;
      background: currentColor;
      opacity: 0.72;
      transform: rotate(-30deg);
    }

    .mdv-code-translate-icon-source,
    .mdv-code-translate-icon-target {
      position: absolute;
      font-size: 11px;
      font-weight: 700;
      line-height: 1;
    }

    .mdv-code-translate-icon-source {
      top: 0;
      left: 0;
    }

    .mdv-code-translate-icon-target {
      right: 0;
      bottom: 0;
    }

    .mdv-code-translate-toggle.is-loading .mdv-code-translate-icon {
      animation: mdv-translate-icon-pulse 900ms ease-in-out infinite alternate;
    }

    @keyframes mdv-translate-icon-pulse {
      from {
        opacity: 0.5;
        transform: scale(0.94);
      }

      to {
        opacity: 1;
        transform: scale(1);
      }
    }

    table {
      width: 100%;
      border-collapse: collapse;
      background: transparent;
      border-radius: 8px;
      overflow: hidden;
    }

    th, td {
      padding: 10px 12px;
      border: 1px solid var(--line);
      text-align: left;
      vertical-align: top;
    }

    th {
      background: var(--panel);
      font-weight: 600;
    }

    blockquote {
      padding: 4px 0 4px 18px;
      border-left: 3px solid var(--quote-border);
      color: var(--quote-ink);
      background: var(--quote-bg);
    }

    hr {
      border: none;
      border-top: 1px solid var(--line);
      margin: 30px 0 24px;
    }

    strong {
      color: var(--ink);
    }

    .internal-tag {
      display: inline-flex;
      align-items: center;
      padding: 0.08em 0.52em;
      margin-right: 0.28em;
      border-radius: 999px;
      background: var(--accent-soft);
      color: var(--accent);
      font-size: 0.92em;
    }

    ::selection {
      background: var(--selection);
    }

    ::-webkit-scrollbar {
      width: 12px;
      height: 12px;
    }

    ::-webkit-scrollbar-track {
      background: transparent;
    }

    ::-webkit-scrollbar-thumb {
      background: var(--scrollbar-thumb);
      border: 3px solid var(--bg);
      border-radius: 999px;
    }

    ::-webkit-scrollbar-thumb:hover {
      background: var(--scrollbar-thumb-hover);
    }

    .mdv-drop-overlay {
      position: fixed;
      inset: 0;
      display: none;
      align-items: center;
      justify-content: center;
      background: var(--drop-overlay-bg);
      z-index: 9999;
      pointer-events: none;
    }

    .mdv-drop-overlay.is-visible {
      display: flex;
    }

    html.mdv-drag-active,
    body.mdv-drag-active,
    .mdv-drop-overlay.is-visible,
    .mdv-drop-pill,
    .mdv-drop-pill * {
      cursor: copy !important;
    }

    .mdv-drop-pill {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 14px 18px;
      border: 1px solid var(--drop-pill-border);
      border-radius: 999px;
      background: var(--drop-pill-bg);
      box-shadow: 0 16px 48px rgba(0, 0, 0, 0.24);
    }

    .mdv-drop-dot {
      width: 8px;
      height: 8px;
      border-radius: 999px;
      background: var(--drop-dot);
      flex: none;
    }

    .mdv-drop-title {
      color: var(--drop-title);
      font-size: 14px;
      font-weight: 600;
    }

    .mdv-drop-copy {
      color: var(--drop-copy);
      font-size: 12px;
    }
  </style>
</head>
<body>
  <main class="page">
    <div class="mdv-line-gutter" id="mdv-line-gutter" aria-hidden="true"></div>
    <div class="pane-title" id="mdv-pane-title"></div>
    <div id="mdv-content"></div>
  </main>
  <div class="mdv-drop-overlay" id="mdv-drop-overlay" aria-hidden="true">
    <div class="mdv-drop-pill">
      <span class="mdv-drop-dot" aria-hidden="true"></span>
      <span class="mdv-drop-title">Drop to open Markdown</span>
      <span class="mdv-drop-copy">.md .markdown .mdx .mkd</span>
    </div>
  </div>
  <script>
    let mdvLineNumberFrame = 0;
    let mdvDragDepth = 0;
    const messageToken = "{{webMessageToken}}";
    const postWebViewMessage = (message, additionalObjects) => {
      if (!window.chrome?.webview) {
        return;
      }

      const payload = { ...message, token: messageToken };
      if (Array.isArray(additionalObjects) &&
          additionalObjects.length > 0 &&
          window.chrome.webview.postMessageWithAdditionalObjects) {
        window.chrome.webview.postMessageWithAdditionalObjects(payload, additionalObjects);
        return;
      }

      window.chrome.webview.postMessage(payload);
    };

    const scheduleLineNumberLayout = () => {
      if (mdvLineNumberFrame) {
        cancelAnimationFrame(mdvLineNumberFrame);
      }

      mdvLineNumberFrame = requestAnimationFrame(() => {
        mdvLineNumberFrame = requestAnimationFrame(() => {
          mdvLineNumberFrame = 0;
          window.mdvRenderLineNumbers();
        });
      });
    };

    const parseDatasetNumber = (element, key) => {
      const value = Number.parseInt(element?.dataset?.[key] ?? "", 10);
      return Number.isFinite(value) ? value : null;
    };

    const parseCssPixelValue = (value) => {
      const parsed = Number.parseFloat(value ?? "");
      return Number.isFinite(parsed) ? parsed : 0;
    };

    const normalizeCodeBlockText = (text) => {
      return typeof text === "string"
        ? text.replace(/\r\n?/g, "\n")
        : "";
    };

    const getTextLineCount = (text) => {
      const normalized = normalizeCodeBlockText(text);
      if (normalized.length === 0) {
        return 0;
      }

      const countedText = normalized.endsWith("\n")
        ? normalized.slice(0, -1)
        : normalized;
      return countedText.length === 0
        ? 1
        : countedText.split("\n").length;
    };

    const splitCodeBlockLines = (text) => {
      const normalized = normalizeCodeBlockText(text);
      if (normalized.length === 0) {
        return [];
      }

      const countedText = normalized.endsWith("\n")
        ? normalized.slice(0, -1)
        : normalized;
      return countedText.length === 0
        ? [""]
        : countedText.split("\n");
    };

    const renderCodeBlockContent = (codeElement, text) => {
      if (!codeElement) {
        return [];
      }

      const lines = splitCodeBlockLines(text);
      const fragment = document.createDocumentFragment();

      lines.forEach((line) => {
        const lineElement = document.createElement("span");
        lineElement.className = "mdv-code-line";
        if (line.length > 0) {
          lineElement.textContent = line;
        } else {
          lineElement.appendChild(document.createElement("br"));
        }

        fragment.appendChild(lineElement);
      });

      codeElement.replaceChildren(fragment);
      return lines;
    };

    const appendLineNumber = (fragment, lineNumber, top) => {
      if (!Number.isFinite(lineNumber) || !Number.isFinite(top)) {
        return;
      }

      const marker = document.createElement("div");
      marker.className = "mdv-line-number";
      marker.textContent = String(lineNumber);
      marker.style.top = `${Math.max(0, top)}px`;
      fragment.appendChild(marker);
    };

    const appendBlankLineNumbersBefore = (fragment, startLine, endLine, previousAnchorTop, nextAnchorTop, lineStep) => {
      if (!Number.isFinite(startLine) || !Number.isFinite(endLine) || endLine < startLine) {
        return;
      }

      const count = endLine - startLine + 1;
      const available = nextAnchorTop - previousAnchorTop;
      const required = (count + 1) * lineStep;
      if (Number.isFinite(previousAnchorTop) && Number.isFinite(nextAnchorTop) && available < required) {
        return;
      }

      for (let lineNumber = endLine; lineNumber >= startLine; lineNumber -= 1) {
        const distance = (endLine - lineNumber + 1) * lineStep;
        appendLineNumber(fragment, lineNumber, nextAnchorTop - distance);
      }
    };

    const appendBlankLineNumbersAfter = (fragment, startLine, endLine, anchorTop, lineStep) => {
      if (!Number.isFinite(startLine) || !Number.isFinite(endLine) || endLine < startLine) {
        return;
      }

      for (let lineNumber = startLine; lineNumber <= endLine; lineNumber += 1) {
        appendLineNumber(fragment, lineNumber, anchorTop + ((lineNumber - startLine + 1) * lineStep));
      }
    };

    window.mdvLineNumberState = {
      sourceLineCount: 0,
      documentPath: ""
    };

    const mdvCodeBlockState = new Map();

    const getCodeBlockContentRange = (block, startLine, endLine) => {
      const codeLineCount = parseDatasetNumber(block, "mdvCodeLineCount");
      if (!Number.isFinite(codeLineCount) || codeLineCount <= 0) {
        return null;
      }

      const totalSourceLines = Math.max(0, endLine - startLine + 1);
      if (totalSourceLines >= codeLineCount + 2) {
        const contentStartLine = Math.max(startLine + 1, endLine - codeLineCount);
        return {
          startLine: contentStartLine,
          endLine: contentStartLine + codeLineCount - 1
        };
      }

      if (totalSourceLines >= codeLineCount) {
        return {
          startLine,
          endLine: startLine + codeLineCount - 1
        };
      }

      return {
        startLine,
        endLine
      };
    };

    const updateCodeBlockTranslateButton = (pre, state) => {
      const button = pre?.querySelector(":scope > .mdv-code-translate-button");
      if (!button || !state) {
        return;
      }

      if (state.isTranslating) {
        button.textContent = "번역 중...";
        button.disabled = true;
        button.title = "코드 블록 텍스트를 번역하고 있습니다";
        return;
      }

      button.disabled = false;
      if (typeof state.translatedText === "string") {
        button.textContent = "원문";
        button.title = "원래 코드 블록 텍스트로 되돌리기";
        return;
      }

      button.textContent = "번역";
      button.title = "이 코드 블록 텍스트 번역";
    };

    const createCodeBlockTranslationLookup = (translations) => {
      const lookup = new Map();
      if (!Array.isArray(translations)) {
        return lookup;
      }

      translations.forEach((entry) => {
        const codeBlockId = entry?.codeBlockId ?? entry?.CodeBlockId ?? "";
        const originalText = entry?.originalText ?? entry?.OriginalText ?? "";
        const translatedTextValue = entry?.translatedText ?? entry?.TranslatedText;
        const isEnabled = entry?.isEnabled ?? entry?.IsEnabled ?? false;

        if (!codeBlockId || typeof originalText !== "string" || !originalText.length) {
          return;
        }

        lookup.set(codeBlockId, {
          originalText: originalText.replace(/\r\n?/g, "\n"),
          translatedText: typeof translatedTextValue === "string"
            ? translatedTextValue.replace(/\r\n?/g, "\n")
            : null,
          isEnabled: isEnabled === true
        });
      });

      return lookup;
    };

    const resolveCodeBlockAnchorTop = (block, pageRect, codeElement, codeLineHeight) => {
      const codeRect = codeElement?.getBoundingClientRect?.();
      const codeTop = Number.isFinite(codeRect?.top)
        ? Math.max(0, codeRect.top - pageRect.top)
        : Math.max(0, block.getBoundingClientRect().top - pageRect.top);
      return codeTop + (codeLineHeight / 2);
    };

    const getCodeBlockLineAnchors = (block, pageRect) => {
      const codeElement = block.querySelector(":scope > code") ?? block;
      const codeLineElements = Array.from(codeElement.querySelectorAll(":scope > .mdv-code-line"));
      if (codeLineElements.length === 0) {
        return null;
      }

      const codeStyle = window.getComputedStyle(codeElement);
      const parsedCodeLineHeight = Number.parseFloat(codeStyle.lineHeight ?? "");
      const parsedCodeFontSize = Number.parseFloat(codeStyle.fontSize ?? "");
      const codeLineHeight = Number.isFinite(parsedCodeLineHeight)
        ? parsedCodeLineHeight
        : ((Number.isFinite(parsedCodeFontSize) ? parsedCodeFontSize : 16) * 1.4);
      const anchors = codeLineElements.map((lineElement) => {
        const lineRect = lineElement.getBoundingClientRect();
        return Math.max(0, lineRect.top - pageRect.top) + (codeLineHeight / 2);
      });

      const blockRect = block.getBoundingClientRect();
      const blockBottom = Math.max(0, blockRect.bottom - pageRect.top);
      return {
        anchors,
        firstAnchor: anchors[0],
        lastAnchor: Math.max(anchors[anchors.length - 1], blockBottom - (codeLineHeight / 2))
      };
    };

    const updateCodeBlockTranslateToggle = (pre, state) => {
      const button = pre?.querySelector(":scope > .mdv-code-translate-toggle");
      if (!button || !state) {
        return;
      }

      button.disabled = state.isTranslating;
      button.classList.toggle("is-on", state.isEnabled);
      button.classList.toggle("is-loading", state.isTranslating);
      button.setAttribute("aria-pressed", state.isEnabled ? "true" : "false");

      const ariaLabel = state.isTranslating
        ? "Translating code block"
        : state.isEnabled
          ? "Disable code block translation"
          : "Enable code block translation";
      button.setAttribute("aria-label", ariaLabel);
      button.title = ariaLabel;
    };

    const enhanceCodeBlocks = (contentElement, documentPath, savedTranslations) => {
      mdvCodeBlockState.clear();
      contentElement.querySelectorAll("pre").forEach((pre, index) => {
        const code = pre.querySelector(":scope > code");
        if (!code) {
          return;
        }

        const originalText = normalizeCodeBlockText(code.textContent ?? "");
        if (!originalText.trim()) {
          return;
        }

        const codeBlockId = `code-${index}-${parseDatasetNumber(pre, "mdvStartLine") ?? index}`;
        pre.classList.add("mdv-code-block");
        pre.dataset.mdvCodeBlockId = codeBlockId;
        pre.dataset.mdvCodeLineCount = String(getTextLineCount(originalText));
        renderCodeBlockContent(code, originalText);

        const savedState = savedTranslations.get(codeBlockId);
        const canRestoreSavedState = savedState?.originalText === originalText;
        const state = {
          originalText,
          translatedText: canRestoreSavedState ? savedState.translatedText : null,
          isEnabled: canRestoreSavedState && savedState.isEnabled === true,
          isTranslating: false
        };
        mdvCodeBlockState.set(codeBlockId, state);

        const toggle = document.createElement("button");
        toggle.type = "button";
        toggle.className = "mdv-code-translate-toggle";
        toggle.setAttribute("aria-pressed", "false");

        const icon = document.createElement("span");
        icon.className = "mdv-code-translate-icon";
        icon.setAttribute("aria-hidden", "true");

        const sourceGlyph = document.createElement("span");
        sourceGlyph.className = "mdv-code-translate-icon-source";
        sourceGlyph.textContent = "A";

        const targetGlyph = document.createElement("span");
        targetGlyph.className = "mdv-code-translate-icon-target";
        targetGlyph.textContent = "\uAC00";

        icon.appendChild(sourceGlyph);
        icon.appendChild(targetGlyph);
        toggle.appendChild(icon);

        toggle.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();

          const currentState = mdvCodeBlockState.get(codeBlockId);
          if (!currentState || currentState.isTranslating) {
            return;
          }

          currentState.isEnabled = !currentState.isEnabled;
          postWebViewMessage({
            type: "set-code-block-translation-enabled",
            codeBlockId,
            text: currentState.originalText,
            documentPath,
            isEnabled: currentState.isEnabled
          });

          if (!currentState.isEnabled) {
            renderCodeBlockContent(code, currentState.originalText);
            updateCodeBlockTranslateToggle(pre, currentState);
            scheduleLineNumberLayout();
            return;
          }

          if (typeof currentState.translatedText === "string") {
            renderCodeBlockContent(code, currentState.translatedText);
            updateCodeBlockTranslateToggle(pre, currentState);
            scheduleLineNumberLayout();
            return;
          }

          currentState.isTranslating = true;
          updateCodeBlockTranslateToggle(pre, currentState);
          postWebViewMessage({
            type: "translate-code-block",
            codeBlockId,
            text: currentState.originalText,
            documentPath
          });
        });

        pre.appendChild(toggle);
        if (state.isEnabled && typeof state.translatedText === "string") {
          renderCodeBlockContent(code, state.translatedText);
        }

        updateCodeBlockTranslateToggle(pre, state);
      });
    };

    window.mdvApplyCodeBlockTranslation = (payload) => {
      const codeBlockId = payload?.codeBlockId ?? payload?.CodeBlockId ?? "";
      if (!codeBlockId) {
        return;
      }

      const pre = document.querySelector(`pre[data-mdv-code-block-id="${codeBlockId}"]`);
      const code = pre?.querySelector(":scope > code");
      const state = mdvCodeBlockState.get(codeBlockId);
      if (!pre || !code || !state) {
        return;
      }

      state.isTranslating = false;
      const isEnabled = payload?.isEnabled ?? payload?.IsEnabled;
      if (typeof isEnabled === "boolean") {
        state.isEnabled = isEnabled;
      }

      const error = payload?.error ?? payload?.Error ?? "";
      if (typeof error === "string" && error.length > 0) {
        renderCodeBlockContent(code, state.originalText);
        updateCodeBlockTranslateToggle(pre, state);
        scheduleLineNumberLayout();
        return;
      }

      const translatedText = payload?.translatedText ?? payload?.TranslatedText ?? "";
      if (typeof translatedText === "string") {
        state.translatedText = translatedText.replace(/\r\n?/g, "\n");
      }

      renderCodeBlockContent(
        code,
        state.isEnabled && typeof state.translatedText === "string"
          ? state.translatedText
          : state.originalText);

      updateCodeBlockTranslateToggle(pre, state);
      scheduleLineNumberLayout();
    };

    window.mdvRenderLineNumbers = () => {
      const pageElement = document.querySelector("main.page");
      const contentElement = document.getElementById("mdv-content");
      const gutterElement = document.getElementById("mdv-line-gutter");

      if (!pageElement || !contentElement || !gutterElement) {
        return;
      }

      gutterElement.replaceChildren();
      gutterElement.style.height = "0px";

      const sourceLineCount = window.mdvLineNumberState?.sourceLineCount ?? 0;
      const blocks = Array.from(contentElement.querySelectorAll("[data-mdv-start-line]"));
      if (sourceLineCount <= 0 || blocks.length === 0) {
        return;
      }

      const pageRect = pageElement.getBoundingClientRect();
      const contentRect = contentElement.getBoundingClientRect();
      const computedStyle = window.getComputedStyle(contentElement);
      const parsedLineHeight = Number.parseFloat(computedStyle.lineHeight ?? "");
      const parsedFontSize = Number.parseFloat(computedStyle.fontSize ?? "");
      const lineHeight = Number.isFinite(parsedLineHeight)
        ? parsedLineHeight
        : ((Number.isFinite(parsedFontSize) ? parsedFontSize : 16) * 1.78);
      const lineNumberStep = Math.max(18, Math.min(lineHeight, 24));
      const resolveBlockLineHeight = (block) => {
        const blockStyle = window.getComputedStyle(block);
        const parsedBlockLineHeight = Number.parseFloat(blockStyle.lineHeight ?? "");
        const parsedBlockFontSize = Number.parseFloat(blockStyle.fontSize ?? "");
        return Number.isFinite(parsedBlockLineHeight)
          ? parsedBlockLineHeight
          : ((Number.isFinite(parsedBlockFontSize) ? parsedBlockFontSize : 16) * 1.4);
      };

      const fragment = document.createDocumentFragment();
      let previousEndLine = 0;
      let previousAnchor = 0;

      blocks.forEach((block, index) => {
        const startLine = parseDatasetNumber(block, "mdvStartLine");
        if (startLine === null) {
          return;
        }

        const endLine = parseDatasetNumber(block, "mdvEndLine") ?? startLine;
        const blockRect = block.getBoundingClientRect();
        const blockTop = Math.max(0, blockRect.top - pageRect.top);
        const blockBottom = Math.max(blockTop, blockRect.bottom - pageRect.top);
        const blockHeight = Math.max(0, blockBottom - blockTop);
        const blockLineHeight = resolveBlockLineHeight(block);
        const blockAnchor = blockTop + (Math.min(blockHeight, blockLineHeight) / 2);
        let firstAnchor = blockAnchor;
        let lastAnchor = blockAnchor;

        if (block.tagName?.toLowerCase() === "pre") {
          const codeRange = getCodeBlockContentRange(block, startLine, endLine);
          if (codeRange) {
            const codeLayout = getCodeBlockLineAnchors(block, pageRect);
            if (codeLayout) {
              firstAnchor = codeLayout.firstAnchor;
              lastAnchor = codeLayout.lastAnchor;
            }
          }
        }

        if (index === 0 && startLine > 1) {
          appendBlankLineNumbersBefore(fragment, 1, startLine - 1, 0, firstAnchor, lineNumberStep);
        }

        if (startLine > previousEndLine + 1) {
          appendBlankLineNumbersBefore(
            fragment,
            previousEndLine + 1,
            startLine - 1,
            previousAnchor,
            firstAnchor,
            lineNumberStep);
        }

        if (block.tagName?.toLowerCase() === "pre") {
          const codeRange = getCodeBlockContentRange(block, startLine, endLine);
          if (codeRange) {
            const codeLayout = getCodeBlockLineAnchors(block, pageRect);
            const anchors = codeLayout?.anchors ?? [];
            const renderCount = Math.min(anchors.length, codeRange.endLine - codeRange.startLine + 1);

            for (let offset = 0; offset < renderCount; offset += 1) {
              appendLineNumber(
                fragment,
                codeRange.startLine + offset,
                anchors[offset]);
            }

            if (renderCount > 0) {
              previousEndLine = Math.max(previousEndLine, codeRange.startLine + renderCount - 1);
              previousAnchor = Math.max(previousAnchor, codeLayout?.lastAnchor ?? previousAnchor);
              return;
            }
          } else {
            appendLineNumber(fragment, startLine, blockAnchor);
          }
        } else {
          appendLineNumber(fragment, startLine, blockAnchor);
        }

        previousEndLine = Math.max(previousEndLine, endLine);
        previousAnchor = lastAnchor;
      });

      if (sourceLineCount > previousEndLine) {
        appendBlankLineNumbersAfter(
          fragment,
          previousEndLine + 1,
          sourceLineCount,
          previousAnchor,
          lineNumberStep);
      }

      gutterElement.appendChild(fragment);
      const trailingLineCount = Math.max(0, sourceLineCount - previousEndLine);
      const lastMarkerBottom = previousAnchor + (trailingLineCount * lineNumberStep) + lineNumberStep;
      const contentBottom = contentElement.offsetTop + contentElement.scrollHeight;
      gutterElement.style.height = `${Math.max(contentBottom, lastMarkerBottom)}px`;
    };

    const hasExternalFiles = (event) => Array.from(event.dataTransfer?.types ?? []).includes("Files");
    const dropOverlayElement = document.getElementById("mdv-drop-overlay");
    const showDropOverlay = () => {
      document.documentElement.classList.add("mdv-drag-active");
      document.body?.classList.add("mdv-drag-active");
      dropOverlayElement?.classList.add("is-visible");
    };
    const hideDropOverlay = () => {
      mdvDragDepth = 0;
      document.documentElement.classList.remove("mdv-drag-active");
      document.body?.classList.remove("mdv-drag-active");
      dropOverlayElement?.classList.remove("is-visible");
    };

    document.addEventListener("dragenter", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
      mdvDragDepth += 1;
      showDropOverlay();
    });

    document.addEventListener("dragover", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
      showDropOverlay();
    });

    document.addEventListener("dragleave", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      mdvDragDepth = Math.max(0, mdvDragDepth - 1);
      if (mdvDragDepth === 0) {
        hideDropOverlay();
      }
    });

    document.addEventListener("drop", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      hideDropOverlay();

      const message = {
        type: "open-dropped-files",
        uriList: event.dataTransfer?.getData("text/uri-list") ?? "",
        plainText: event.dataTransfer?.getData("text/plain") ?? ""
      };
      const files = Array.from(event.dataTransfer?.files ?? []);

      postWebViewMessage(message, files);
    });

    window.addEventListener("blur", hideDropOverlay);

    window.addEventListener("resize", () => {
      if ((window.mdvLineNumberState?.sourceLineCount ?? 0) > 0) {
        scheduleLineNumberLayout();
      }
    });

    window.mdvRenderDocument = (payload) => {
      const htmlBody = payload?.htmlBody ?? payload?.HtmlBody ?? "";
      const baseUri = payload?.baseUri ?? payload?.BaseUri ?? "about:blank";
      const paneTitle = payload?.paneTitle ?? payload?.PaneTitle ?? "";
      const fileName = payload?.fileName ?? payload?.FileName ?? "MD Translator Viewer";
      const sourceLineCount = payload?.sourceLineCount ?? payload?.SourceLineCount ?? 0;
      const sourcePath = payload?.sourcePath ?? payload?.SourcePath ?? "";
      const codeBlockTranslations = payload?.codeBlockTranslations ?? payload?.CodeBlockTranslations ?? [];
      const baseElement = document.getElementById("mdv-base");
      const paneTitleElement = document.getElementById("mdv-pane-title");
      const contentElement = document.getElementById("mdv-content");

      if (baseElement) {
        baseElement.setAttribute("href", baseUri);
      }

      document.title = fileName;

      if (paneTitleElement) {
        paneTitleElement.textContent = paneTitle;
      }

      if (contentElement) {
        contentElement.innerHTML = htmlBody;
        enhanceCodeBlocks(contentElement, sourcePath, createCodeBlockTranslationLookup(codeBlockTranslations));
        contentElement.querySelectorAll("img").forEach((image) => {
          if (!image.complete) {
            image.addEventListener("load", scheduleLineNumberLayout, { once: true });
            image.addEventListener("error", scheduleLineNumberLayout, { once: true });
          }
        });
      }

      window.mdvLineNumberState = {
        sourceLineCount: Number.isFinite(sourceLineCount) ? sourceLineCount : 0,
        documentPath: sourcePath
      };
      scheduleLineNumberLayout();
    };

    const pointerNavigationButtonToDirection = (button) => {
      if (button === 3) {
        return -1;
      }

      if (button === 4) {
        return 1;
      }

      return 0;
    };

    const suppressPointerNavigationEvent = (event) => {
      const direction = pointerNavigationButtonToDirection(event.button);
      if (direction === 0) {
        return 0;
      }

      event.preventDefault();
      event.stopPropagation();
      if (typeof event.stopImmediatePropagation === "function") {
        event.stopImmediatePropagation();
      }

      return direction;
    };

    document.addEventListener("mousedown", (event) => {
      suppressPointerNavigationEvent(event);
    }, true);

    document.addEventListener("mouseup", (event) => {
      const direction = suppressPointerNavigationEvent(event);
      if (direction === 0) {
        return;
      }

      postWebViewMessage({
        type: "navigate-history",
        direction
      });
    }, true);

    document.addEventListener("auxclick", (event) => {
      suppressPointerNavigationEvent(event);
    }, true);

    document.addEventListener("click", (event) => {
      const link = event.target.closest("a[href]");
      if (!link) {
        return;
      }

      const rawHref = link.getAttribute("href") || "";
      if (rawHref.startsWith("#")) {
        event.preventDefault();
        const target = document.getElementById(rawHref.slice(1));
        if (target) {
          target.scrollIntoView({ behavior: "smooth", block: "start" });
        }
        return;
      }

      if (window.chrome && window.chrome.webview) {
        event.preventDefault();
        postWebViewMessage({
          type: "open-link",
          href: link.href
        });
      }
    });
  </script>
</body>
</html>
""";
    }

    public string RenderEmpty(string message, string webMessageToken, ViewerDocumentTheme theme)
    {
        var safeMessage = WebUtility.HtmlEncode(message);
        return $$"""
<!doctype html>
<html lang="ko">
<head>
  <meta charset="utf-8">
  <style>
    :root {
{{BuildThemeCssVariables(theme)}}
    }

    body {
      margin: 0;
      display: grid;
      place-items: center;
      min-height: 100vh;
      font-family: "Segoe UI", "Malgun Gothic", sans-serif;
      background: var(--bg);
      color: var(--ink);
    }
    .box {
      padding: 18px 20px;
      border: 1px solid var(--line);
      border-radius: 10px;
      background: var(--panel);
    }

    .mdv-drop-overlay {
      position: fixed;
      inset: 0;
      display: none;
      align-items: center;
      justify-content: center;
      background: var(--drop-overlay-bg);
      pointer-events: none;
    }

    .mdv-drop-overlay.is-visible {
      display: flex;
    }

    html.mdv-drag-active,
    body.mdv-drag-active,
    .mdv-drop-overlay.is-visible,
    .mdv-drop-pill,
    .mdv-drop-pill * {
      cursor: copy !important;
    }

    .mdv-drop-pill {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 14px 18px;
      border: 1px solid var(--drop-pill-border);
      border-radius: 999px;
      background: var(--drop-pill-bg);
      box-shadow: 0 16px 48px rgba(0, 0, 0, 0.24);
    }

    .mdv-drop-dot {
      width: 8px;
      height: 8px;
      border-radius: 999px;
      background: var(--drop-dot);
      flex: none;
    }

    .mdv-drop-title {
      color: var(--drop-title);
      font-size: 14px;
      font-weight: 600;
    }

    .mdv-drop-copy {
      color: var(--drop-copy);
      font-size: 12px;
    }
  </style>
</head>
  <body>
  <div class="box">{{safeMessage}}</div>
  <div class="mdv-drop-overlay" id="mdv-drop-overlay" aria-hidden="true">
    <div class="mdv-drop-pill">
      <span class="mdv-drop-dot" aria-hidden="true"></span>
      <span class="mdv-drop-title">Drop to open Markdown</span>
      <span class="mdv-drop-copy">.md .markdown .mdx .mkd</span>
    </div>
  </div>
  <script>
    let mdvDragDepth = 0;
    const messageToken = "{{webMessageToken}}";
    const postWebViewMessage = (message, additionalObjects) => {
      if (!window.chrome?.webview) {
        return;
      }

      const payload = { ...message, token: messageToken };
      if (Array.isArray(additionalObjects) &&
          additionalObjects.length > 0 &&
          window.chrome.webview.postMessageWithAdditionalObjects) {
        window.chrome.webview.postMessageWithAdditionalObjects(payload, additionalObjects);
        return;
      }

      window.chrome.webview.postMessage(payload);
    };
    const dropOverlayElement = document.getElementById("mdv-drop-overlay");
    const hasExternalFiles = (event) => Array.from(event.dataTransfer?.types ?? []).includes("Files");
    const showDropOverlay = () => {
      document.documentElement.classList.add("mdv-drag-active");
      document.body?.classList.add("mdv-drag-active");
      dropOverlayElement?.classList.add("is-visible");
    };
    const hideDropOverlay = () => {
      mdvDragDepth = 0;
      document.documentElement.classList.remove("mdv-drag-active");
      document.body?.classList.remove("mdv-drag-active");
      dropOverlayElement?.classList.remove("is-visible");
    };

    document.addEventListener("dragenter", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
      mdvDragDepth += 1;
      showDropOverlay();
    });

    document.addEventListener("dragover", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
      showDropOverlay();
    });

    document.addEventListener("dragleave", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      mdvDragDepth = Math.max(0, mdvDragDepth - 1);
      if (mdvDragDepth === 0) {
        hideDropOverlay();
      }
    });

    document.addEventListener("drop", (event) => {
      if (!hasExternalFiles(event)) {
        return;
      }

      event.preventDefault();
      hideDropOverlay();

      const message = {
        type: "open-dropped-files",
        uriList: event.dataTransfer?.getData("text/uri-list") ?? "",
        plainText: event.dataTransfer?.getData("text/plain") ?? ""
      };
      const files = Array.from(event.dataTransfer?.files ?? []);

      postWebViewMessage(message, files);
    });

    window.addEventListener("blur", hideDropOverlay);
  </script>
</body>
</html>
""";
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static string RenderMarkdownBody(string markdown, MarkdownPipeline pipeline)
    {
        var document = Markdown.Parse(markdown, pipeline);
        var html = Markdown.ToHtml(markdown, pipeline);
        var lineStartOffsets = BuildLineStartOffsets(markdown);
        var markers = CollectLineMarkers(document, lineStartOffsets);
        return InjectLineMarkers(html, markers);
    }

    private static int CountSourceLines(string markdown)
    {
        return BuildLineStartOffsets(markdown).Count;
    }

    private static List<int> BuildLineStartOffsets(string markdown)
    {
        var lineStartOffsets = new List<int> { 0 };
        for (var index = 0; index < markdown.Length; index++)
        {
            if (markdown[index] == '\n' && index + 1 < markdown.Length)
            {
                lineStartOffsets.Add(index + 1);
            }
        }

        return lineStartOffsets;
    }

    private static List<LineMarker> CollectLineMarkers(ContainerBlock container, List<int> lineStartOffsets)
    {
        var markers = new List<LineMarker>();
        CollectLineMarkers(container, markers, lineStartOffsets);
        return markers;
    }

    private static void CollectLineMarkers(ContainerBlock container, List<LineMarker> markers, List<int> lineStartOffsets)
    {
        foreach (var block in container)
        {
            if (block.Line >= 0 && TryGetLineMarker(block, out var marker))
            {
                markers.Add(marker with
                {
                    StartLine = block.Line + 1,
                    EndLine = Math.Max(block.Line + 1, GetLineNumber(lineStartOffsets, block.Span.End)),
                });
            }

            if (block is ContainerBlock childContainer)
            {
                CollectLineMarkers(childContainer, markers, lineStartOffsets);
            }
        }
    }

    private static int GetLineNumber(List<int> lineStartOffsets, int position)
    {
        if (position < 0 || lineStartOffsets.Count == 0)
        {
            return 1;
        }

        var index = lineStartOffsets.BinarySearch(position);
        return index >= 0
            ? index + 1
            : Math.Max(1, ~index);
    }

    private static bool TryGetLineMarker(Block block, out LineMarker marker)
    {
        marker = block switch
        {
            HeadingBlock headingBlock => new LineMarker($"h{headingBlock.Level}", 0, 0),
            ParagraphBlock when !HasContainerLineNumberAncestor(block.Parent) => new LineMarker("p", 0, 0),
            ListItemBlock => new LineMarker("li", 0, 0),
            QuoteBlock => new LineMarker("blockquote", 0, 0),
            CodeBlock => new LineMarker("pre", 0, 0),
            ThematicBreakBlock => new LineMarker("hr", 0, 0),
            Table => new LineMarker("table", 0, 0),
            _ => default,
        };

        return !string.IsNullOrWhiteSpace(marker.TagName);
    }

    private static bool HasContainerLineNumberAncestor(Block? block)
    {
        for (var current = block; current is not null; current = current.Parent)
        {
            if (current is ListItemBlock or QuoteBlock or CodeBlock or Table)
            {
                return true;
            }
        }

        return false;
    }

    private static string InjectLineMarkers(string html, IReadOnlyList<LineMarker> markers)
    {
        if (markers.Count == 0)
        {
            return html;
        }

        var builder = new StringBuilder(html.Length + (markers.Count * 56));
        var currentIndex = 0;
        var markerIndex = 0;

        foreach (Match match in LineNumberTagRegex.Matches(html))
        {
            builder.Append(html, currentIndex, match.Index - currentIndex);

            var tagName = match.Groups["tag"].Value.ToLowerInvariant();
            if (markerIndex < markers.Count &&
                string.Equals(markers[markerIndex].TagName, tagName, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(InjectLineMarkerIntoOpeningTag(match.Value, markers[markerIndex]));
                markerIndex++;
            }
            else
            {
                builder.Append(match.Value);
            }

            currentIndex = match.Index + match.Length;
        }

        builder.Append(html, currentIndex, html.Length - currentIndex);
        return builder.ToString();
    }

    private static string InjectLineMarkerIntoOpeningTag(string openingTag, LineMarker marker)
    {
        var insertAt = openingTag.LastIndexOf('>');
        if (insertAt < 0)
        {
            return openingTag;
        }

        var startLineValue = marker.StartLine.ToString(CultureInfo.InvariantCulture);
        var endLineValue = marker.EndLine.ToString(CultureInfo.InvariantCulture);
        return openingTag.Insert(
            insertAt,
            $" data-mdv-start-line=\"{startLineValue}\" data-mdv-end-line=\"{endLineValue}\"");
    }
}

internal sealed class DocumentRenderPayload
{
    public string HtmlBody { get; init; } = string.Empty;

    public string BaseUri { get; init; } = string.Empty;

    public string PaneTitle { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public int SourceLineCount { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public IReadOnlyList<CodeBlockTranslationPayload> CodeBlockTranslations { get; init; } = Array.Empty<CodeBlockTranslationPayload>();
}

internal sealed class CodeBlockTranslationPayload
{
    public string CodeBlockId { get; init; } = string.Empty;

    public string OriginalText { get; init; } = string.Empty;

    public string? TranslatedText { get; init; }

    public bool IsEnabled { get; init; }
}

internal readonly record struct LineMarker(string TagName, int StartLine, int EndLine);
