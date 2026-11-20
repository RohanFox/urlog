import { LOADER, MARK, MARK_SMALL, SMALL_CUT_MAX, WORDMARK } from './geometry';
import { TAGLINE_PATH } from './tagline';
import './brand.css';

interface MarkProps {
  /** Height in pixels. At 24 and under the small cut is drawn. */
  size: number;
  /** A name for assistive technology. Without one the mark is decoration. */
  label?: string;
}

function labelling(label: string | undefined) {
  return label === undefined ? ({ 'aria-hidden': true } as const) : ({ role: 'img', 'aria-label': label } as const);
}

/** The mark alone. */
export function Mark({ size, label }: MarkProps) {
  const cut = size <= SMALL_CUT_MAX ? MARK_SMALL : MARK;
  return (
    <svg className="brand-mark" viewBox={cut.viewBox} height={size} width={Math.round((size * cut.width) / cut.height)} {...labelling(label)}>
      <g className="brand-ink" strokeWidth={cut.stroke}>
        <polygon points={cut.first} />
        <polygon points={cut.second} />
      </g>
      <polygon className="brand-diamond" strokeWidth={cut.diamondStroke} points={cut.diamond} />
    </svg>
  );
}

interface LockupProps {
  /** With the words LOG FORENSICS under the name. */
  full?: boolean;
  label?: string;
}

/** The mark with the name beside it. Its height comes from the class of its place (header, sign-in). */
export function Lockup({ full = false, label = full ? 'urlog, log forensics' : 'urlog' }: LockupProps) {
  return (
    <svg className={full ? 'brand-lockup brand-lockup-full' : 'brand-lockup'} viewBox={WORDMARK.viewBox} role="img" aria-label={label}>
      <g transform={WORDMARK.markTransform}>
        <g className="brand-ink" strokeWidth={MARK.stroke}>
          <polygon points={MARK.first} />
          <polygon points={MARK.second} />
        </g>
        <polygon className="brand-diamond" strokeWidth={MARK.diamondStroke} points={MARK.diamond} />
      </g>
      <g className="brand-word" transform={WORDMARK.wordTransform} strokeWidth={full ? WORDMARK.strokeFull : WORDMARK.strokeSmall}>
        {WORDMARK.paths.map((d) => (
          <path key={d} d={d} />
        ))}
        {WORDMARK.circles.map((c) => (
          <circle key={c.cx} cx={c.cx} cy={c.cy} r={c.r} />
        ))}
      </g>
      {full ? <path className="brand-tagline" d={TAGLINE_PATH} opacity={WORDMARK.taglineOpacity} /> : null}
    </svg>
  );
}

interface LoaderProps {
  /** 16 inline, 40 in a panel, 64 for a whole page. */
  size: 16 | 40 | 64;
  /** On a filled button the loader takes the button's text colour. */
  onAccent?: boolean;
  label?: string;
}

/**
 * The loading animation: a dotted line traces each arrowhead, each fills, the diamond arrives with a ring, an echo
 * of the first arrowhead slides back. With reduced motion the mark stands still and whole.
 */
export function Loader({ size, onAccent = false, label }: LoaderProps) {
  const classes = ['brand-loader', size === 16 ? 'brand-loader-small' : 'brand-loader-glow', onAccent ? 'brand-loader-on' : ''].join(' ').trim();
  return (
    <svg className={classes} viewBox={LOADER.viewBox} height={size} width={Math.round((size * LOADER.width) / LOADER.height)} {...labelling(label)}>
      <polygon className="brand-loader-echo" points={MARK.first} />
      <polygon className="brand-loader-line" pathLength={1} points={LOADER.firstOutline} />
      <polygon className="brand-loader-line brand-loader-line-2" pathLength={1} points={LOADER.secondOutline} />
      <polygon className="brand-loader-solid" points={MARK.first} />
      <polygon className="brand-loader-solid brand-loader-solid-2" points={MARK.second} />
      <polygon className="brand-loader-ring" points={MARK.diamond} />
      <polygon className="brand-loader-dot" points={MARK.diamond} />
    </svg>
  );
}

/** The whole-page loading screen, the same one index.html paints before any script has run. */
export function BootScreen() {
  return (
    <div className="boot" role="status" aria-label="urlog is loading">
      <Loader size={64} />
      <span className="boot-word" aria-hidden="true">
        LOADING
      </span>
    </div>
  );
}
