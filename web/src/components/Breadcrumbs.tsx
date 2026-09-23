import { Fragment } from 'react';
import { Link } from 'react-router-dom';
import { IconChevronRight } from './icons';

export interface Crumb {
  label: string;
  to?: string;
}

export function Breadcrumbs({ crumbs }: { crumbs: Crumb[] }) {
  if (crumbs.length === 0) return null;
  return (
    <nav className="breadcrumbs" aria-label="Breadcrumb">
      <ol>
        {crumbs.map((crumb, i) => {
          const last = i === crumbs.length - 1;
          return (
            <Fragment key={`${i}-${crumb.label}`}>
              <li className={last ? 'current' : undefined} aria-current={last ? 'page' : undefined}>
                {crumb.to && !last ? <Link to={crumb.to}>{crumb.label}</Link> : <span title={crumb.label}>{crumb.label}</span>}
              </li>
              {!last && (
                <li className="sep" aria-hidden="true">
                  <IconChevronRight />
                </li>
              )}
            </Fragment>
          );
        })}
      </ol>
    </nav>
  );
}
