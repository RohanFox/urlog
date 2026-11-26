// Line icons on a 24 grid, drawn for this interface. They take the colour of the text around them.
const PATHS = {
  plus: 'M12 5v14M5 12h14',
  check: 'M5 12.5 10 17.5 19 7.5',
  alert: 'M12 3.5 21.5 20h-19ZM12 10v4.5M12 17.2v.3',
  info: 'M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17ZM12 11v5.5M12 7.6v.3',
  retry: 'M19.5 12a7.5 7.5 0 1 1-2.4-5.5M19.5 4.5v4.5H15',
  close: 'M6.5 6.5 17.5 17.5M17.5 6.5 6.5 17.5',
  folder: 'M3.5 6.5h6l2 2.5h9v10.5h-17Z',
  down: 'M6.5 9.5 12 15l5.5-5.5',
  up: 'M6.5 14.5 12 9l5.5 5.5',
  left: 'M14.5 6.5 9 12l5.5 5.5',
  right: 'M9.5 6.5 15 12l-5.5 5.5',
  out: 'M14 4.5h5.5v15H14M10.5 8 14.5 12l-4 4M14.5 12h-10',
  upload: 'M12 16.5V5M7.5 9.5 12 5l4.5 4.5M4.5 19.5h15',
  file: 'M6.5 3.5h7.5l4 4v13h-11.5ZM14 3.5v4h4',
  pause: 'M9 5.5v13M15 5.5v13',
  play: 'M8 5.5v13l10.5-6.5Z',
  copy: 'M9 9h10.5v10.5H9ZM5.5 15h-1V4.5H15v1',
  download: 'M12 4.5V16M7.5 11.5 12 16l4.5-4.5M4.5 19.5h15',
  search: 'M10.5 4.5a6 6 0 1 0 0 12 6 6 0 0 0 0-12ZM15 15l4.5 4.5',
  // A disc of radius 4 with eight rays from 6.5 to 8.5, the reach of the circle in "info".
  sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM12 3.5v2M12 18.5v2M3.5 12h2M18.5 12h2M6 6l1.4 1.4M16.6 16.6 18 18M18 6l-1.4 1.4M7.4 16.6 6 18',
  // The circle of "info" with a bite of radius 6.5 taken from its upper right.
  moon: 'M12 3.5a8.5 8.5 0 1 0 8.5 8.5 6.5 6.5 0 0 1-8.5-8.5Z',
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({ name }: { name: IconName }) {
  return (
    <svg className="icon" viewBox="0 0 24 24" aria-hidden="true">
      <path d={PATHS[name]} />
    </svg>
  );
}
