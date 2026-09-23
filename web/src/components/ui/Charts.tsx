import { useId, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';

/*
 * Small dependency-free SVG/HTML charts. Marks are thin, colours come from CSS variables
 * (light + dark), every chart has a text alternative, and nothing relies on colour alone
 * (values are printed beside each mark or in the legend).
 */

export interface Slice {
  key: string;
  label: string;
  value: number;
  /** CSS colour, usually a var(--chart-*) token. */
  color: string;
}

export function DonutChart({ slices, centerValue, centerLabel, label }: { slices: Slice[]; centerValue: ReactNode; centerLabel: string; label: string }) {
  const total = slices.reduce((s, x) => s + x.value, 0);
  const R = 52;
  const C = 2 * Math.PI * R;
  let offset = 0;
  const [hover, setHover] = useState<string | null>(null);
  const active = slices.find((s) => s.key === hover);

  return (
    <div className="donut">
      <div className="donut-figure">
        <svg viewBox="0 0 140 140" role="img" aria-label={`${label}: ${slices.filter((s) => s.value > 0).map((s) => `${s.label} ${s.value}`).join(', ')}`}>
          <circle cx="70" cy="70" r={R} fill="none" stroke="var(--chart-track)" strokeWidth="14" />
          {total > 0 &&
            slices
              .filter((s) => s.value > 0)
              .map((s) => {
                const len = (s.value / total) * C;
                const gap = slices.filter((x) => x.value > 0).length > 1 ? 2 : 0;
                const el = (
                  <circle
                    key={s.key}
                    cx="70"
                    cy="70"
                    r={R}
                    fill="none"
                    stroke={s.color}
                    strokeWidth={hover === s.key ? 16 : 14}
                    strokeDasharray={`${Math.max(len - gap, 0.5)} ${C - Math.max(len - gap, 0.5)}`}
                    strokeDashoffset={-offset}
                    transform="rotate(-90 70 70)"
                    onMouseEnter={() => setHover(s.key)}
                    onMouseLeave={() => setHover(null)}
                    style={{ transition: 'stroke-width 150ms' }}
                  />
                );
                offset += len;
                return el;
              })}
        </svg>
        <div className="donut-center">
          <strong>{active ? active.value : centerValue}</strong>
          <span>{active ? active.label : centerLabel}</span>
        </div>
      </div>
      <ul className="chart-legend">
        {slices.map((s) => (
          <li key={s.key} onMouseEnter={() => setHover(s.key)} onMouseLeave={() => setHover(null)}>
            <span className="chart-swatch" style={{ background: s.color }} aria-hidden="true" />
            <span className="chart-legend-label">{s.label}</span>
            <strong>{s.value}</strong>
          </li>
        ))}
      </ul>
    </div>
  );
}

export interface BarItem {
  key: string;
  label: string;
  value: number;
  /** 0-100 fill; defaults to value / max. */
  percent?: number;
  display?: string;
  to?: string;
  color?: string;
}

/** Horizontal bar list: label · thin bar · value. */
export function BarList({ items, max, label }: { items: BarItem[]; max?: number; label: string }) {
  const top = max ?? Math.max(1, ...items.map((i) => i.value));
  return (
    <ul className="bar-list" aria-label={label}>
      {items.map((i) => {
        const pct = Math.min(100, Math.max(0, i.percent ?? (i.value / top) * 100));
        const name = i.to ? <Link to={i.to}>{i.label}</Link> : <span>{i.label}</span>;
        return (
          <li key={i.key}>
            <span className="bar-list-label" title={i.label}>
              {name}
            </span>
            <span className="bar-list-track" role="img" aria-label={`${i.label}: ${i.display ?? i.value}`}>
              <span className="bar-list-fill" style={{ width: `${pct}%`, background: i.color }} />
            </span>
            <strong className="bar-list-value">{i.display ?? i.value}</strong>
          </li>
        );
      })}
    </ul>
  );
}

export interface ColumnItem {
  key: string;
  label: string;
  value: number;
  color: string;
}

/** Vertical colourful columns; the value is printed above each bar. */
export function ColumnChart({ items, label }: { items: ColumnItem[]; label: string }) {
  const top = Math.max(1, ...items.map((i) => i.value));
  return (
    <div className="column-chart" role="img" aria-label={`${label}: ${items.map((i) => `${i.label} ${i.value}`).join(', ')}`}>
      {items.map((i) => (
        <div key={i.key} className="column-chart-col" title={`${i.label}: ${i.value}`}>
          <span className="column-chart-value">{i.value}</span>
          <span className="column-chart-bar" style={{ height: `${(i.value / top) * 75}%`, background: `linear-gradient(180deg, ${i.color}, color-mix(in srgb, ${i.color} 55%, transparent))` }} />
          <span className="column-chart-label">{i.label}</span>
        </div>
      ))}
    </div>
  );
}

export interface StackedRow {
  key: string;
  label: string;
  to?: string;
  segments: { key: string; label: string; value: number; color: string }[];
}

/** One multi-coloured stacked bar per row, with a shared legend. */
export function StackedBars({ rows, legend, label }: { rows: StackedRow[]; legend: { key: string; label: string; color: string }[]; label: string }) {
  const top = Math.max(1, ...rows.map((r) => r.segments.reduce((s, x) => s + x.value, 0)));
  return (
    <>
      <ul className="stacked-list" aria-label={label}>
        {rows.map((r) => {
          const total = r.segments.reduce((s, x) => s + x.value, 0);
          const summary = r.segments.filter((s) => s.value > 0).map((s) => `${s.label} ${s.value}`).join(', ') || 'none';
          return (
            <li key={r.key}>
              <span className="bar-list-label" title={r.label}>
                {r.to ? <Link to={r.to}>{r.label}</Link> : <span>{r.label}</span>}
              </span>
              <span className="stacked-track" role="img" aria-label={`${r.label}: ${summary}`}>
                {r.segments
                  .filter((s) => s.value > 0)
                  .map((s) => (
                    <span key={s.key} className="stacked-seg" title={`${s.label}: ${s.value}`} style={{ width: `${(s.value / top) * 100}%`, background: s.color }} />
                  ))}
              </span>
              <strong className="bar-list-value">{total}</strong>
            </li>
          );
        })}
      </ul>
      <ul className="chart-legend inline" style={{ marginTop: '1rem' }}>
        {legend.map((l) => (
          <li key={l.key}>
            <span className="chart-swatch" style={{ background: l.color }} aria-hidden="true" />
            <span className="chart-legend-label">{l.label}</span>
          </li>
        ))}
      </ul>
    </>
  );
}

/** Small progress ring with the percentage printed in the middle. */
export function RingGauge({ percent, color, label }: { percent: number; color: string; label: string }) {
  const R = 26;
  const C = 2 * Math.PI * R;
  const p = Math.min(100, Math.max(0, percent));
  return (
    <div className="insight-ring" role="img" aria-label={`${label}: ${p}%`}>
      <svg viewBox="0 0 64 64">
        <circle cx="32" cy="32" r={R} fill="none" stroke="var(--chart-track)" strokeWidth="7" />
        <circle cx="32" cy="32" r={R} fill="none" stroke={color} strokeWidth="7" strokeLinecap="round" strokeDasharray={`${(p / 100) * C} ${C}`} transform="rotate(-90 32 32)" />
      </svg>
      <strong>{p}%</strong>
    </div>
  );
}

export interface SeriesPoint {
  label: string;
  value: number;
}

/** Single-series area/line chart with a hover crosshair. */
export function AreaChart({ points, label, unit }: { points: SeriesPoint[]; label: string; unit: string }) {
  const W = 640;
  const H = 200;
  const pad = { l: 34, r: 12, t: 12, b: 26 };
  const [hover, setHover] = useState<number | null>(null);
  const gid = useId().replace(/:/g, '');
  if (points.length === 0) return null;

  const maxV = Math.max(1, ...points.map((p) => p.value));
  const niceMax = maxV <= 4 ? maxV : Math.ceil(maxV / 4) * 4;
  const x = (i: number) => pad.l + (points.length === 1 ? (W - pad.l - pad.r) / 2 : (i / (points.length - 1)) * (W - pad.l - pad.r));
  const y = (v: number) => pad.t + (1 - v / niceMax) * (H - pad.t - pad.b);
  const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(p.value).toFixed(1)}`).join(' ');
  const area = `${line} L${x(points.length - 1).toFixed(1)},${y(0)} L${x(0).toFixed(1)},${y(0)} Z`;
  const ticks = [0, niceMax / 2, niceMax].filter((t, i, a) => a.indexOf(t) === i);
  const labelEvery = Math.ceil(points.length / 6);

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const px = ((e.clientX - rect.left) / rect.width) * W;
    let best = 0;
    points.forEach((_, i) => {
      if (Math.abs(x(i) - px) < Math.abs(x(best) - px)) best = i;
    });
    setHover(best);
  };
  const h = hover !== null ? points[hover] : null;

  return (
    <div className="area-chart">
      <svg
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label={`${label}: ${points.map((p) => `${p.label} ${p.value}`).join(', ')}`}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
      >
        {ticks.map((t) => (
          <g key={t}>
            <line x1={pad.l} x2={W - pad.r} y1={y(t)} y2={y(t)} stroke="var(--chart-grid)" strokeWidth="1" />
            <text x={pad.l - 8} y={y(t) + 4} textAnchor="end" className="chart-axis">
              {Math.round(t)}
            </text>
          </g>
        ))}
        <defs>
          <linearGradient id={`${gid}-fill`} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--chart-c4)" stopOpacity="0.35" />
            <stop offset="100%" stopColor="var(--chart-c5)" stopOpacity="0.02" />
          </linearGradient>
          <linearGradient id={`${gid}-line`} x1="0" y1="0" x2="1" y2="0">
            <stop offset="0%" stopColor="var(--chart-c1)" />
            <stop offset="100%" stopColor="var(--chart-c4)" />
          </linearGradient>
        </defs>
        <path d={area} fill={`url(#${gid}-fill)`} />
        <path d={line} fill="none" stroke={`url(#${gid}-line)`} strokeWidth="2.5" strokeLinejoin="round" strokeLinecap="round" />
        {points.map((p, i) =>
          i % labelEvery === 0 || i === points.length - 1 ? (
            <text key={i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'} className="chart-axis">
              {p.label}
            </text>
          ) : null,
        )}
        {h && hover !== null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={y(0)} stroke="var(--border-strong)" strokeDasharray="3 3" />
            <circle cx={x(hover)} cy={y(h.value)} r="4.5" fill="var(--chart-1)" stroke="var(--surface)" strokeWidth="2" />
          </g>
        )}
      </svg>
      {h && hover !== null && (
        <div className="chart-tooltip" style={{ left: `${(x(hover) / W) * 100}%` }}>
          <span>{h.label}</span>
          <strong>
            {h.value} {unit}
          </strong>
        </div>
      )}
    </div>
  );
}
