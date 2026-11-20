// The DOM helpers behind the original-document views: hostile document markup is made inert, and the
// cited text is marked by moving existing text into <mark>, never by writing HTML.
import { describe, expect, test, vi } from 'vitest';
import { markAcross, markOccurrence, sanitizeRenderedDocument, unmarkAll } from '../../src/lib/documentHighlight';
import { docxBlocks } from '../../src/lib/documentRenderers';

// docx-preview stand-in that renders what a malicious DOCX could make it produce.
vi.mock('docx-preview', () => ({
  renderAsync: async (_data: unknown, body: HTMLElement, styles: HTMLElement) => {
    styles.innerHTML = '<style>.docx{color:black}</style><style>@import url(https://evil.test/x.css);</style>';
    body.innerHTML = `
      <div class="docx-wrapper"><section class="docx">
        <header><p>Header text</p></header>
        <article>
          <p>3.2 User Roles</p>
          <p onclick="alert(1)">Administrators <a href="javascript:alert(2)">manage</a> access.</p>
          <script>window.__pwned = true</script>
          <iframe srcdoc="<script>parent.__pwned = true</script>"></iframe>
          <p><img src="https://evil.test/beacon.png" onerror="alert(3)"><img src="blob:local-image"></p>
          <p><a href="https://example.test/" target="_blank">external</a> <a href="#_Toc1">jump</a></p>
          <ol><li><p>Footnote text</p></li></ol>
        </article>
        <footer><p>Footer text</p></footer>
      </section></div>`;
  },
}));

describe('sanitizeRenderedDocument', () => {
  test('removes scripts, frames, handlers, external links and loads, and keeps the document', async () => {
    const { renderDocx } = await import('../../src/lib/documentRenderers');
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const doc = frame.contentDocument!;

    const { blocks, paragraphs } = await renderDocx(new ArrayBuffer(8), doc.body, doc.head);

    const html = doc.documentElement.innerHTML;
    expect(doc.querySelector('script, iframe')).toBeNull();
    expect(html).not.toMatch(/onclick|onerror|javascript:|evil\.test|target=/i);
    expect(doc.querySelector('img[src="blob:local-image"]')).not.toBeNull(); // the document's own image
    expect(doc.querySelector('a[href="#_Toc1"]')).not.toBeNull(); // an in-document link
    expect(doc.querySelectorAll('style')).toHaveLength(1);
    expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
    // Body paragraphs only, in order: not the header, footer or notes the extracted text lacks.
    expect(paragraphs.map((p) => p.element.textContent?.trim())).toEqual(['3.2 User Roles', 'Administrators manage access.', '', 'external jump']);
    // With no table, the blocks are those same paragraphs.
    expect(blocks.map((b) => b.element)).toEqual(paragraphs.map((p) => p.element));
    frame.remove();
  });

  test('neutralises style-based loads too', () => {
    const root = document.createElement('div');
    root.innerHTML = '<p style="background:url(https://evil.test/a.png)">x</p><p style="color:red">y</p><form action="https://evil.test"><input></form>';
    sanitizeRenderedDocument(root);
    expect(root.innerHTML).toBe('<p>x</p><p style="color:red">y</p>');
  });
});

describe('markOccurrence', () => {
  test('marks text split across formatting runs, leaving the text and markup around it intact', () => {
    const p = document.createElement('p');
    p.innerHTML = '<b>12.7 Decl</b><i>ine Categories</i> are listed.';
    const marks = markOccurrence(p, '12.7 Decline Categories');
    expect(marks.map((m) => m.textContent)).toEqual(['12.7 Decl', 'ine Categories']);
    expect(p.textContent).toBe('12.7 Decline Categories are listed.');
    expect(p.querySelectorAll('b mark, i mark')).toHaveLength(2);
  });

  test('marks the requested match of a repeated text, not the first', () => {
    const page = document.createElement('div');
    page.innerHTML = '<span>Scope</span><span>Billing scope.</span><span>Scope</span><span>Security scope.</span>';
    // Matches are counted inside longer text too: the third match is the second heading.
    const [mark] = markOccurrence(page, 'Scope', 2);
    expect(mark.parentElement).toBe(page.children[2]);
    expect(markOccurrence(page, 'Scope', 9)).toEqual([]);
  });

  test('marks nothing when the text is not there, and treats markup-like text as text', () => {
    const p = document.createElement('p');
    p.textContent = '<img src=x onerror=alert(1)> Section 3.2';
    expect(markOccurrence(p, 'Section 9.9')).toEqual([]);
    const [mark] = markOccurrence(p, '<img src=x onerror=alert(1)>');
    expect(mark.textContent).toBe('<img src=x onerror=alert(1)>');
    expect(p.querySelector('img')).toBeNull();
  });
});

describe('markAcross', () => {
  const paragraphs = (...html: string[]) => {
    const article = document.createElement('article');
    article.innerHTML = html.map((h) => `<p style="font-size:11pt">${h}</p>`).join('');
    return Array.from(article.querySelectorAll('p'));
  };

  test('marks only the quoted words inside one paragraph, keeping its formatting', () => {
    const [p] = paragraphs('The eight <b>decline categories</b> are listed in the decline popup.');
    const marks = markAcross([p], 'decline categories are listed');
    expect(marks.map((m) => m.textContent)).toEqual(['decline categories', ' are listed']);
    expect(p.textContent).toBe('The eight decline categories are listed in the decline popup.');
    expect(p.querySelector('b mark')).not.toBeNull(); // the bold run is kept, the mark sits inside it
    expect(p.getAttribute('style')).toBe('font-size:11pt');
  });

  test('a quote over two paragraphs marks its words in each, and nothing else of them', () => {
    const blocks = paragraphs('12.7 Decline Categories', 'The eight decline categories are listed.');
    const marks = markAcross(blocks, 'Decline Categories The eight decline');
    expect(marks.map((m) => m.textContent)).toEqual(['Decline Categories', 'The eight decline']);
    expect(marks.map((m) => m.parentElement)).toEqual(blocks);
    expect(blocks.map((b) => b.textContent)).toEqual(['12.7 Decline Categories', 'The eight decline categories are listed.']);
  });

  test('marks nothing and leaves the paragraphs untouched when the words are not there', () => {
    const blocks = paragraphs('Records are kept.', 'For seven years.');
    const before = blocks.map((b) => b.innerHTML);
    expect(markAcross(blocks, 'kept for ten years')).toEqual([]);
    expect(markAcross([], 'anything')).toEqual([]);
    expect(blocks.map((b) => b.innerHTML)).toEqual(before);
  });

  test('two marks on the same text keep their own classes (a related passage beside the shown one)', () => {
    const [p] = paragraphs('Invoices are sent monthly. Passwords are stored hashed.');
    markAcross([p], 'Invoices are sent monthly.', 'src-hit');
    markAcross([p], 'Passwords are stored hashed.', 'src-hit src-hit-related');
    expect(Array.from(p.querySelectorAll('mark')).map((m) => [m.className, m.textContent])).toEqual([
      ['src-hit', 'Invoices are sent monthly.'],
      ['src-hit src-hit-related', 'Passwords are stored hashed.'],
    ]);
  });
});

describe('docxBlocks', () => {
  test('reads each table row as one block of cells, line breaks as spaces, and keeps every paragraph for older sources', () => {
    const body = document.createElement('div');
    body.innerHTML = `
      <section><article>
        <p>Appendix B</p>
        <table><tr><td><p>Leave type</p></td><td><p>Approver</p></td></tr>
          <tr><td><p>Annual</p><p>leave</p></td><td><p>Line<br>manager</p><table><tr><td><p>nested</p></td></tr></table></td></tr></table>
        <p>After</p>
        <ol><li><p>Footnote</p></li></ol>
      </article></section>`;
    const { blocks, paragraphs } = docxBlocks(body);
    expect(blocks.map((b) => b.text)).toEqual(['Appendix B', 'Leave type | Approver', 'Annual leave | Line manager nested', 'After']);
    expect(blocks[2].element.tagName).toBe('TR');
    expect(paragraphs.map((p) => p.text)).toEqual(['Appendix B', 'Leave type', 'Approver', 'Annual', 'leave', 'Line manager', 'nested', 'After']);
  });
});

describe('unmarkAll', () => {
  const block = (html: string) => {
    const p = document.createElement('p');
    p.innerHTML = html;
    return p;
  };

  test('takes the marks out again and leaves the text and formatting exactly as before', () => {
    const p = block('The eight <b>decline categories</b> are listed in the decline popup.');
    const before = p.innerHTML;
    markAcross([p], 'decline categories are listed');
    markOccurrence(p, 'popup', 0, 'src-hit src-hit-related');
    expect(p.querySelectorAll('mark')).toHaveLength(3);

    unmarkAll(p);

    expect(p.querySelector('mark')).toBeNull();
    expect(p.innerHTML).toBe(before);
    expect(Array.from(p.childNodes).filter((n) => n.nodeType === 3).map((n) => (n as Text).data)).toEqual(['The eight ', ' are listed in the decline popup.']);
  });

  test('handles a mark inside another, and the same words can be marked again', () => {
    const p = block('Passwords are stored hashed.');
    markOccurrence(p, 'Passwords are stored hashed.', 0, 'src-hit src-hit-related');
    markOccurrence(p, 'stored', 0);
    unmarkAll(p);
    expect(p.innerHTML).toBe('Passwords are stored hashed.');
    expect(markOccurrence(p, 'are stored').map((m) => m.textContent)).toEqual(['are stored']);
  });

  test('leaves marks the document has of its own, and takes the heading underline out too', () => {
    const p = block('<mark class="doc-own">Draft</mark> 3.2 User Roles');
    markOccurrence(p, '3.2 User Roles', 0, 'src-heading-mark');
    unmarkAll(p);
    expect(p.innerHTML).toBe('<mark class="doc-own">Draft</mark> 3.2 User Roles');
  });
});
