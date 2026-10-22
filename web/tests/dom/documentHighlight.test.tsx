// The DOM helpers behind the original-document views: hostile document markup is made inert, and the
// cited text is marked by moving existing text into <mark>, never by writing HTML.
import { describe, expect, test, vi } from 'vitest';
import { markOccurrence, sanitizeRenderedDocument } from '../../src/lib/documentHighlight';

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

    const paragraphs = await renderDocx(new ArrayBuffer(8), doc.body, doc.head);

    const html = doc.documentElement.innerHTML;
    expect(doc.querySelector('script, iframe')).toBeNull();
    expect(html).not.toMatch(/onclick|onerror|javascript:|evil\.test|target=/i);
    expect(doc.querySelector('img[src="blob:local-image"]')).not.toBeNull(); // the document's own image
    expect(doc.querySelector('a[href="#_Toc1"]')).not.toBeNull(); // an in-document link
    expect(doc.querySelectorAll('style')).toHaveLength(1);
    expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
    // Body paragraphs only, in order: not the header, footer or notes the extracted text lacks.
    expect(paragraphs.map((p) => p.textContent?.trim())).toEqual(['3.2 User Roles', 'Administrators manage access.', '', 'external jump']);
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
