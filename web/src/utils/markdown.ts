/**
 * Lightweight, zero-dependency Markdown-to-HTML renderer for Deadlock Playground changelogs.
 * Supports headers (# to ######), horizontal rules (---, ***, ___), blockquotes (>),
 * fenced code blocks (```), nested unordered/ordered lists, and inline formatting
 * (**bold**, *italic*, ~~strike~~, `code`, [link](url)).
 */

const CHECK_ICON_SVG =
  '<svg class="md-check-icon" xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="m9 12 2 2 4-4"/></svg>';

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function sanitizeUrl(url: string): string {
  const trimmed = url.trim();
  if (/^(https?:\/\/|\/|#)/i.test(trimmed)) {
    return escapeHtml(trimmed);
  }
  return '#';
}

function formatInline(rawText: string): string {
  // Protect inline code spans before escaping/formatting
  const codeSpans: string[] = [];
  let text = rawText.replace(/`([^`]+?)`/g, (_m, code) => {
    const idx = codeSpans.length;
    codeSpans.push(`<code class="md-code">${escapeHtml(code)}</code>`);
    return `\u0000CODE${idx}\u0000`;
  });

  text = escapeHtml(text);

  // Links [label](url)
  text = text.replace(/\[([^\]]+?)\]\(([^)]+?)\)/g, (_m, label, url) => {
    const safeUrl = sanitizeUrl(url);
    return `<a class="md-link" href="${safeUrl}" target="_blank" rel="noopener noreferrer">${label}</a>`;
  });

  // Bold + Italic (***text***)
  text = text.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');

  // Bold (**text** or __text__)
  text = text.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
  text = text.replace(/(^|[^\w])__(.+?)__(?=[^\w]|$)/g, '$1<strong>$2</strong>');

  // Italic (*text* or _text_ bounded by non-word chars to preserve snake_case)
  text = text.replace(/\*(.+?)\*/g, '<em>$1</em>');
  text = text.replace(/(^|[^\w])_(.+?)_(?=[^\w]|$)/g, '$1<em>$2</em>');

  // Strikethrough (~~text~~)
  text = text.replace(/~~(.+?)~~/g, '<del>$1</del>');

  // Restore inline code spans
  for (let i = 0; i < codeSpans.length; i++) {
    text = text.replace(`\u0000CODE${i}\u0000`, codeSpans[i]);
  }

  return text;
}

export function renderMarkdownToHtml(markdown: string): string {
  if (!markdown) return '';

  const lines = markdown.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const html: string[] = [];

  let inCodeBlock = false;
  let codeLines: string[] = [];
  let listMode: 'ul' | 'ol' | null = null;
  let inSubList = false;

  const closeLists = () => {
    if (inSubList) {
      html.push('</ul></li>');
      inSubList = false;
    }
    if (listMode === 'ul') {
      html.push('</ul>');
    } else if (listMode === 'ol') {
      html.push('</ol>');
    }
    listMode = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const rawLine = lines[i];
    const trimmed = rawLine.trim();

    // Fenced code blocks
    if (trimmed.startsWith('```')) {
      if (!inCodeBlock) {
        closeLists();
        inCodeBlock = true;
        codeLines = [];
      } else {
        html.push(`<pre class="md-pre"><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
        inCodeBlock = false;
        codeLines = [];
      }
      continue;
    }

    if (inCodeBlock) {
      codeLines.push(rawLine);
      continue;
    }

    // Blank line
    if (trimmed.length === 0) {
      closeLists();
      continue;
    }

    // Gracefully unwrap accidental "* ## Header" or "- ---"
    const unwrapped = trimmed.replace(/^[-*•]\s+(?=(?:#{1,6}\s+|(?:---+|===+|\*\*\*+|___+)$))/, '');

    // Horizontal rule
    if (/^(?:-{3,}|={3,}|\*{3,}|_{3,})$/.test(unwrapped)) {
      closeLists();
      html.push('<hr class="md-hr" />');
      continue;
    }

    // Headers (# to ######)
    const headerMatch = unwrapped.match(/^(#{1,6})\s+(.+)$/);
    if (headerMatch) {
      closeLists();
      const level = headerMatch[1].length;
      const tag = level <= 2 ? 'h4' : level === 3 ? 'h5' : 'h6';
      html.push(`<${tag} class="md-heading md-h${level}">${formatInline(headerMatch[2].trim())}</${tag}>`);
      continue;
    }

    // Blockquote
    const quoteMatch = trimmed.match(/^>\s?(.*)$/);
    if (quoteMatch) {
      closeLists();
      html.push(`<blockquote class="md-quote">${formatInline(quoteMatch[1])}</blockquote>`);
      continue;
    }

    // Ordered list
    const orderedMatch = rawLine.match(/^(\s*)(\d+)\.\s+(.+)$/);
    if (orderedMatch) {
      if (inSubList) {
        html.push('</ul></li>');
        inSubList = false;
      }
      if (listMode !== 'ol') {
        if (listMode === 'ul') html.push('</ul>');
        html.push('<ol class="md-ol">');
        listMode = 'ol';
      }
      html.push(`<li class="md-ol-item">${formatInline(orderedMatch[3])}</li>`);
      continue;
    }

    // Unordered list (*, -, +, •)
    const bulletMatch = rawLine.match(/^(\s*)[-*+•]\s+(.+)$/);
    if (bulletMatch) {
      const indentSpaces = bulletMatch[1].replace(/\t/g, '  ').length;
      const content = formatInline(bulletMatch[2]);

      if (indentSpaces >= 2 && listMode === 'ul') {
        if (!inSubList) {
          // Attach nested list inside last <li> if possible, or open a nested block
          const lastIdx = html.length - 1;
          if (lastIdx >= 0 && html[lastIdx].endsWith('</li>')) {
            html[lastIdx] = html[lastIdx].slice(0, -5);
            html.push('<ul class="md-sublist">');
            inSubList = true;
          } else {
            html.push('<li class="md-sublist-wrapper"><ul class="md-sublist">');
            inSubList = true;
          }
        }
        html.push(`<li class="md-subitem">${content}</li>`);
      } else {
        if (inSubList) {
          html.push('</ul></li>');
          inSubList = false;
        }
        if (listMode !== 'ul') {
          if (listMode === 'ol') html.push('</ol>');
          html.push('<ul class="md-list">');
          listMode = 'ul';
        }
        html.push(`<li class="md-item">${CHECK_ICON_SVG}<span>${content}</span></li>`);
      }
      continue;
    }

    // Plain paragraph
    closeLists();
    html.push(`<p class="md-p">${formatInline(trimmed)}</p>`);
  }

  if (inCodeBlock && codeLines.length > 0) {
    html.push(`<pre class="md-pre"><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
  }
  closeLists();

  return html.join('\n');
}

export function renderChangelogEntryHtml(entry: { markdown?: string; highlights?: string[] }): string {
  if (entry.markdown && entry.markdown.trim().length > 0) {
    return renderMarkdownToHtml(entry.markdown);
  }

  const highlights = entry.highlights ?? [];
  const normalizedLines = highlights.map((raw) => {
    const trimmedRight = raw.replace(/\s+$/, '');
    const trimmed = trimmedRight.trim();
    if (!trimmed) return '';
    if (
      /^#{1,6}\s+/.test(trimmed) ||
      /^(?:-{3,}|={3,}|\*{3,}|_{3,})$/.test(trimmed) ||
      /^>\s?/.test(trimmed) ||
      /^\d+\.\s+/.test(trimmed) ||
      /^(\s*)[-*+•]\s+/.test(trimmedRight) ||
      trimmed.startsWith('```')
    ) {
      return trimmedRight;
    }
    return `* ${trimmed}`;
  });

  return renderMarkdownToHtml(normalizedLines.join('\n'));
}

