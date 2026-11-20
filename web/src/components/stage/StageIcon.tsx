import type { ComponentType, SVGProps } from 'react';
import type { StageKey } from '../../lib/stages';
import { IconCheckCircle, IconCode, IconEye, IconLayers, IconLink, IconSearch, IconShieldCheck, IconSparkles, IconUpload } from '../icons';

const ICONS: Record<StageKey, ComponentType<SVGProps<SVGSVGElement>>> = {
  gathering: IconUpload,
  analysis: IconSearch,
  validation: IconCheckCircle,
  design: IconLayers,
  development: IconCode,
  testing: IconShieldCheck,
  traceability: IconLink,
  copilot: IconSparkles,
  review: IconEye,
};

export function StageIcon({ stage, ...props }: { stage: StageKey } & SVGProps<SVGSVGElement>) {
  const Icon = ICONS[stage];
  return <Icon aria-hidden="true" {...props} />;
}
