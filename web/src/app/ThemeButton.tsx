import { Icon } from '../ui/Icon';
import { useTheme } from './theme';

/** One button in the top bar. Its icon and name say what a press does, so it has no pressed state. */
export function ThemeButton() {
  const { theme, toggle } = useTheme();
  const label = theme === 'dark' ? 'Switch to the light theme' : 'Switch to the dark theme';
  return (
    <button type="button" className="btn btn-ghost theme-button" aria-label={label} title={label} onClick={toggle}>
      <Icon name={theme === 'dark' ? 'sun' : 'moon'} />
    </button>
  );
}
