// Word pages in a narrow pane (measureDocxLayout / fitDocxToWidth): the page reflows to the pane instead of
// being cut off - fixed pt widths become proportions, indents shrink, a too-wide image box is zoomed - and
// is restored when the pane is wide enough again. jsdom has no layout, so element sizes are stubbed from
// data attributes (data-w: the element's width as rendered, data-cw: the room inside it).
import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import { DOCX_PAGE_GUTTER, firstColumnCells, fitDocxToWidth, markTableOverflow, measureDocxLayout, revealInTable } from '../../src/lib/documentRenderers';

const saved = {
  offsetWidth: Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetWidth'),
  clientWidth: Object.getOwnPropertyDescriptor(Element.prototype, 'clientWidth'),
  rect: Element.prototype.getBoundingClientRect,
  scrollWidth: Object.getOwnPropertyDescriptor(Element.prototype, 'scrollWidth'),
  scrollLeft: Object.getOwnPropertyDescriptor(Element.prototype, 'scrollLeft'),
};
const scrolled = new WeakMap<Element, number>();
beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, get() { return Number((this as HTMLElement).dataset.w ?? 0); } });
  Object.defineProperty(Element.prototype, 'clientWidth', { configurable: true, get() { return Number((this as HTMLElement).dataset?.cw ?? 0); } });
  // data-x: the element's left edge in the table's content (so a scrolled table moves it left).
  Element.prototype.getBoundingClientRect = function (this: HTMLElement) {
    const width = Number(this.dataset?.w ?? 0);
    const table = this.closest?.('table');
    const left = Number(this.dataset?.x ?? 0) - (table && table !== this ? scrolled.get(table) ?? 0 : 0);
    return { width, height: 10, top: 0, left, right: left + width, bottom: 10, x: left, y: 0, toJSON: () => ({}) } as DOMRect;
  };
  Object.defineProperty(Element.prototype, 'scrollWidth', { configurable: true, get() { return Number((this as HTMLElement).dataset?.sw ?? 0); } });
  Object.defineProperty(Element.prototype, 'scrollLeft', {
    configurable: true,
    get() { return scrolled.get(this as Element) ?? 0; },
    set(v: number) { scrolled.set(this as Element, v); },
  });
});
afterAll(() => {
  if (saved.offsetWidth) Object.defineProperty(HTMLElement.prototype, 'offsetWidth', saved.offsetWidth);
  if (saved.clientWidth) Object.defineProperty(Element.prototype, 'clientWidth', saved.clientWidth);
  Element.prototype.getBoundingClientRect = saved.rect;
  if (saved.scrollWidth) Object.defineProperty(Element.prototype, 'scrollWidth', saved.scrollWidth);
  if (saved.scrollLeft) Object.defineProperty(Element.prototype, 'scrollLeft', saved.scrollLeft);
});

// An A4 page as docx-preview lays it out: 794px with 96px margins (602px of text), a list at two levels,
// a full-width three-column table, a narrow table, and a cropped picture in its box.
function page() {
  const body = document.createElement('div');
  body.innerHTML = `
    <div class="docx-wrapper"><section class="docx" data-w="794" style="padding: 96px 96px; width: 595pt">
      <article data-cw="300">
        <p id="intro">Scope of the enrolment rules.</p>
        <p id="level1" style="margin-left: 48px; text-indent: -24px">First level</p>
        <p id="level3" style="margin-left: 144px; text-indent: -24px">Third level</p>
        <table id="wide" data-w="602" style="width: 451.3pt"><colgroup><col style="width: 150px"><col style="width: 150px"><col style="width: 302px"></colgroup>
          <tr><td data-w="150" style="width: 112pt"><p>Surgeon</p></td><td data-w="150" style="width: 112pt"><p>A provider role</p></td><td data-w="302" style="width: 226pt"><p id="cell">Same standard ownership rule</p></td></tr>
        </table>
        <table id="narrow" data-w="200" style="width: 150pt"><tr><td data-w="200" style="width: 150pt"><p>Code</p></td></tr></table>
        <p id="figure" data-cw="300"><span><div id="box" data-w="570" style="width: 427pt; height: 75pt"><img style="width: 427pt; clip-path: rect(10% 90% 100% 10%)"></div></span></p>
      </article>
    </section></div>`;
  document.body.append(body);
  return body;
}
const el = (body: HTMLElement, id: string) => body.querySelector<HTMLElement>(`#${id}`)!;

describe('Word pages in a narrow pane', () => {
  test('narrower than the page: reflows to proportions of the pane - text, elements and order untouched', () => {
    const body = page();
    const text = body.textContent;
    const cell = el(body, 'cell');
    const layout = measureDocxLayout(body)!;
    expect(layout.page).toBe(794);
    expect(layout.text).toBe(602);

    expect(fitDocxToWidth(layout, document.documentElement, 320)).toBe(true);

    expect(document.documentElement.classList.contains('src-docx-reflow')).toBe(true);
    // Tables and their columns keep their Word proportions, as percentages.
    expect(el(body, 'wide').style.width).toBe('100%');
    expect(Array.from(body.querySelectorAll<HTMLElement>('#wide col')).map((c) => Math.round(parseFloat(c.style.width)))).toEqual([25, 25, 50]);
    expect(Array.from(body.querySelectorAll<HTMLElement>('#wide td')).map((c) => Math.round(parseFloat(c.style.width)))).toEqual([25, 25, 50]);
    expect(Math.round(parseFloat(el(body, 'narrow').style.width))).toBe(33);
    // Indents shrink with the text width (300 of 602), never below their hanging number or bullet.
    expect(parseFloat(el(body, 'level3').style.marginLeft)).toBeCloseTo(144 * (300 / 602), 1);
    expect(el(body, 'level1').style.marginLeft).toBe('24px');
    // The cropped picture's box is zoomed to the room it has; the picture inside is left as drawn.
    expect(Number(el(body, 'box').style.getPropertyValue('zoom'))).toBeCloseTo(300 / 570, 3);
    expect(body.querySelector('img')!.getAttribute('style')).toBe('width: 427pt; clip-path: rect(10% 90% 100% 10%)');
    // A table still wider than its room scrolls on its own; one that fits does not.
    expect(el(body, 'wide').classList.contains('src-docx-wide')).toBe(true);
    expect(el(body, 'narrow').classList.contains('src-docx-wide')).toBe(false);

    expect(body.textContent).toBe(text);
    expect(el(body, 'cell')).toBe(cell);
    body.remove();
  });

  test('as wide as the page: nothing changes, and a reflowed page is restored exactly', () => {
    const body = page();
    const before = body.innerHTML;
    const layout = measureDocxLayout(body)!;

    expect(fitDocxToWidth(layout, document.documentElement, 794 + DOCX_PAGE_GUTTER)).toBe(false);
    expect(document.documentElement.classList.contains('src-docx-reflow')).toBe(false);
    expect(body.innerHTML).toBe(before);

    fitDocxToWidth(layout, document.documentElement, 320);
    expect(body.innerHTML).not.toBe(before);
    expect(fitDocxToWidth(layout, document.documentElement, 1200)).toBe(false);
    expect(document.documentElement.classList.contains('src-docx-reflow')).toBe(false);
    expect(el(body, 'wide').style.width).toBe('451.3pt');
    expect(el(body, 'level3').style.marginLeft).toBe('144px');
    expect(el(body, 'box').style.getPropertyValue('zoom')).toBe('');
    expect(el(body, 'wide').classList.contains('src-docx-wide')).toBe(false);
    body.remove();
  });

  test('no pane width yet (not laid out) or no page: nothing is reflowed', () => {
    const body = page();
    const layout = measureDocxLayout(body)!;
    expect(fitDocxToWidth(layout, document.documentElement, 0)).toBe(false);
    expect(document.documentElement.classList.contains('src-docx-reflow')).toBe(false);
    body.remove();

    const empty = document.createElement('div');
    empty.innerHTML = '<div class="docx-wrapper"></div>';
    expect(measureDocxLayout(empty)).toBeNull();
  });
});

// A page with one many-column table that cannot fit a 320px pane: 4 columns (first one narrow), a title
// row merged across all of them, and a row whose first cell spans two rows.
function tablePage(firstWidth = 90) {
  const body = document.createElement('div');
  const cell = (id: string, x: number, w: number, extra = '') => `<td id="${id}" data-x="${x}" data-w="${w}" style="width: ${w}px"${extra}><p>${id}</p></td>`;
  body.innerHTML = `
    <div class="docx-wrapper"><section class="docx" data-w="794" style="padding: 96px 96px; width: 595pt">
      <article data-cw="300">
        <table id="grid" data-w="602" data-cw="300" data-sw="602" style="width: 451.3pt"><tbody>
          <tr><td id="title" colspan="4" data-w="602"><p>Roles and permissions</p></td></tr>
          <tr>${cell('role1', 0, firstWidth, ' rowspan="2"')}${cell('a1', firstWidth, 170)}${cell('b1', firstWidth + 170, 170)}${cell('c1', firstWidth + 340, 170)}</tr>
          <tr>${cell('a2', firstWidth, 170)}${cell('b2', firstWidth + 170, 170)}${cell('c2', firstWidth + 340, 170)}</tr>
          <tr>${cell('role3', 0, firstWidth)}${cell('a3', firstWidth, 170)}${cell('b3', firstWidth + 170, 170)}<td id="c3" data-x="${firstWidth + 340}" data-w="170"><p><mark id="hit" data-x="${firstWidth + 350}" data-w="40">rule</mark></p></td></tr>
        </tbody></table>
      </article>
    </section></div>`;
  document.body.append(body);
  return body;
}

describe('Word tables that scroll sideways', () => {
  test('the first column is found as a grid: merged rows and columns are not mistaken for it', () => {
    const body = tablePage();
    const { cells, columns } = firstColumnCells(el(body, 'grid') as HTMLTableElement);
    expect(columns).toBe(4);
    // Not the merged title (it spans every column), nor a2 (first in its row, but in the second column).
    expect(cells.map((c) => c.id)).toEqual(['role1', 'role3']);
    body.remove();
  });

  test('a narrow first column is pinned, the far side is marked, and both are cleared when the page fits', () => {
    const body = tablePage();
    const text = body.textContent;
    const layout = measureDocxLayout(body)!;
    fitDocxToWidth(layout, document.documentElement, 320);
    const grid = el(body, 'grid');
    expect(grid.classList.contains('src-docx-wide')).toBe(true);
    expect(grid.classList.contains('src-docx-pinned')).toBe(true);
    expect(Array.from(body.querySelectorAll('.src-docx-sticky')).map((c) => c.id)).toEqual(['role1', 'role3']);
    expect(el(body, 'role1').style.getPropertyValue('--src-sticky-bg')).not.toBe('');
    expect(grid.classList.contains('src-docx-more-right')).toBe(true);
    expect(grid.classList.contains('src-docx-more-left')).toBe(false);
    expect(body.textContent).toBe(text);

    fitDocxToWidth(layout, document.documentElement, 1200);
    expect(body.querySelectorAll('.src-docx-sticky')).toHaveLength(0);
    expect(el(body, 'role1').style.cssText).toBe('width: 90px;');
    expect(grid.className).toBe('');
    body.remove();
  });

  test('a wide first column is not pinned: it would cover most of what the table shows', () => {
    const body = tablePage(160); // 160 of 300px shown
    const layout = measureDocxLayout(body)!;
    fitDocxToWidth(layout, document.documentElement, 320);
    expect(el(body, 'grid').classList.contains('src-docx-wide')).toBe(true);
    expect(el(body, 'grid').classList.contains('src-docx-pinned')).toBe(false);
    expect(body.querySelectorAll('.src-docx-sticky')).toHaveLength(0);
    fitDocxToWidth(layout, document.documentElement, 1200);
    body.remove();
  });

  test('the cited cell is scrolled into view clear of the pinned column, and the edges follow the scroll', () => {
    const body = tablePage();
    const layout = measureDocxLayout(body)!;
    fitDocxToWidth(layout, document.documentElement, 320);
    const grid = el(body, 'grid');
    revealInTable(el(body, 'hit'));
    // The mark starts at 440px; the pinned column (90px) and a 16px margin stay to its left.
    expect(grid.scrollLeft).toBe(440 - 90 - 16);
    expect(grid.classList.contains('src-docx-more-left')).toBe(true);
    expect(grid.classList.contains('src-docx-more-right')).toBe(false);

    grid.scrollLeft = 0;
    markTableOverflow(grid);
    expect(grid.classList.contains('src-docx-more-left')).toBe(false);
    expect(grid.classList.contains('src-docx-more-right')).toBe(true);

    // A citation in the pinned column itself is always in view: the table is not moved.
    revealInTable(el(body, 'role3'));
    expect(grid.scrollLeft).toBe(0);
    fitDocxToWidth(layout, document.documentElement, 1200);
    body.remove();
  });
});
