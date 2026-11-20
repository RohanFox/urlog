// The geometry of the mark: two arrowheads and a diamond. Every drawing of the logo reads these constants,
// and a test holds the favicon and the first-paint markup in index.html to the same points.

/** The mark at 32 px and above. */
export const MARK = {
  viewBox: '0 -4 126 108',
  width: 126,
  height: 108,
  first: '52,0 78,0 28,50 78,100 52,100 2,50',
  second: '98,0 124,0 74,50 124,100 98,100 48,50',
  diamond: '92.6,50 105.6,37 118.6,50 105.6,63',
  stroke: 4,
  diamondStroke: 2,
} as const;

/** The small cut, for 24 px and under: the arrowheads stand further apart and the diamond is larger. */
export const MARK_SMALL = {
  viewBox: '0 -2 135 104',
  width: 135,
  height: 104,
  first: '52,0 78,0 28,50 78,100 52,100 2,50',
  second: '104,0 130,0 80,50 130,100 104,100 54,50',
  diamond: '105,50 119,36 133,50 119,64',
  stroke: 3,
  diamondStroke: 2,
} as const;

/** The largest height in pixels at which the small cut is used. */
export const SMALL_CUT_MAX = 24;

/** The word "urlog" as strokes, placed to the right of the mark in a lockup. */
export const WORDMARK = {
  viewBox: '0 0 508 140',
  width: 508,
  height: 140,
  markTransform: 'translate(0,10) scale(0.9)',
  wordTransform: 'translate(162,0)',
  paths: ['M0 40V74A26 26 0 0 0 52 74M52 40V100', 'M88 100V66A26 26 0 0 1 114 40H122', 'M146 10V100', 'M338 40V106A24 24 0 0 1 314 130H290'],
  circles: [
    { cx: 212, cy: 70, r: 30 },
    { cx: 308, cy: 70, r: 30 },
  ],
  /** Heavier at header size, lighter with the tagline. */
  strokeSmall: 13,
  strokeFull: 11,
  taglineOpacity: 0.72,
} as const;

/** The loader traces each arrowhead as an open outline before it fills. */
export const LOADER = {
  viewBox: '-4 -6 132 112',
  width: 132,
  height: 112,
  bootViewBox: '-34 -24 194 148',
  firstOutline: '28,50 78,0 52,0 2,50 52,100 78,100',
  secondOutline: '74,50 124,0 98,0 48,50 98,100 124,100',
} as const;

/** The favicon: a dark tile with the small cut, in the colours of the logo sheet. */
export const FAVICON = { size: 32, radius: 7, tile: '#0E0F12', ink: '#ECEEF2', diamond: '#5B7CFF' } as const;
