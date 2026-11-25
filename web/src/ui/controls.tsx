import { useEffect, useId, useRef, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react';
import { Loader } from '../brand/Logo';
import { Icon, type IconName } from './Icon';
import './ui.css';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  kind?: 'default' | 'primary' | 'ghost';
  small?: boolean;
  /** Work is under way: the loader shows and the button takes no second press. */
  busy?: boolean;
  icon?: IconName;
}

export function Button({ kind = 'default', small = false, busy = false, icon, children, type = 'button', disabled, ...rest }: ButtonProps) {
  const classes = ['btn', kind === 'default' ? '' : `btn-${kind}`, small ? 'btn-small' : ''].filter(Boolean).join(' ');
  return (
    <button {...rest} type={type} className={classes} aria-busy={busy} disabled={disabled === true || busy}>
      {busy ? <Loader size={16} onAccent={kind === 'primary'} /> : icon === undefined ? null : <Icon name={icon} />}
      {children}
    </button>
  );
}

interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string;
  /** A line under the input: what the server said about this value. */
  error?: string | undefined;
  hint?: string | undefined;
}

export function Field({ label, error, hint, ...rest }: FieldProps) {
  const id = useId();
  const noteId = `${id}-note`;
  const note = error ?? hint;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input {...rest} id={id} className={error === undefined ? 'input' : 'input input-bad'} aria-invalid={error !== undefined} aria-describedby={note === undefined ? undefined : noteId} />
      {note === undefined ? null : (
        <span id={noteId} className={error === undefined ? 'sub' : 'field-error'}>
          {note}
        </span>
      )}
    </div>
  );
}

interface BannerProps {
  /** `error`: something failed. `notice`: a correct answer the user must know about (a refusal, a wait). */
  kind: 'error' | 'notice';
  title: string;
  children?: ReactNode;
  action?: ReactNode;
}

export function Banner({ kind, title, children, action }: BannerProps) {
  return (
    <div className={kind === 'error' ? 'banner banner-error' : 'banner'} role={kind === 'error' ? 'alert' : 'status'}>
      <Icon name={kind === 'error' ? 'alert' : 'info'} />
      <div className="banner-text">
        <strong>{title}</strong>
        {children === undefined ? null : <p>{children}</p>}
      </div>
      {action}
    </div>
  );
}

export function Tag({ children, accent = false }: { children: ReactNode; accent?: boolean }) {
  return <span className={accent ? 'tag tag-accent' : 'tag'}>{children}</span>;
}

/** The loader with a line of text beside it, for a region that is being read. */
export function LoadingBlock({ title }: { title: string }) {
  return (
    <div className="loading-block" role="status">
      <Loader size={40} />
      <strong>{title}</strong>
    </div>
  );
}

const SKELETON_WIDTHS = ['skel-w60', 'skel-w80', 'skel-w40', 'skel-w70', 'skel-w50'] as const;

/** Placeholder rows of a table whose data has not arrived. They carry no text and are hidden from a screen reader. */
export function SkeletonRows({ rows, columns }: { rows: number; columns: number }) {
  return (
    <>
      {Array.from({ length: rows }, (_, r) => (
        <tr key={r} aria-hidden="true">
          {Array.from({ length: columns }, (_, c) => (
            <td key={c}>
              <span className={`skel ${SKELETON_WIDTHS[(r + c) % SKELETON_WIDTHS.length] ?? ''}`} />
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <h2>{title}</h2>
      {children}
    </div>
  );
}

interface DialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children: ReactNode;
}

/**
 * A modal dialog on the element the browser has for it: the browser keeps the focus inside, makes the page
 * behind inert and closes on Escape, and nothing has to be injected into the document to do so.
 */
export function Dialog({ open, ...rest }: DialogProps) {
  // Mounted only while open, so what was typed into it does not outlive it.
  return open ? <Modal {...rest} /> : null;
}

function Modal({ onOpenChange, title, description, children }: Omit<DialogProps, 'open'>) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descriptionId = useId();
  useEffect(() => {
    const el = ref.current;
    if (el === null) {
      return;
    }
    // A document without the modal API (a test's) still shows the dialog.
    if (typeof el.showModal === 'function') {
      el.showModal();
    } else {
      el.setAttribute('open', '');
    }
    return () => {
      if (el.open && typeof el.close === 'function') {
        el.close();
      }
    };
  }, []);
  return (
    <dialog
      ref={ref}
      className="dialog"
      aria-labelledby={titleId}
      aria-describedby={description === undefined ? undefined : descriptionId}
      onCancel={(event) => {
        event.preventDefault();
        onOpenChange(false);
      }}
    >
      <div className="dialog-head">
        <h2 id={titleId} className="dialog-title">
          {title}
        </h2>
        <button
          type="button"
          className="btn btn-ghost btn-small btn-icon"
          aria-label="Close"
          onClick={() => {
            onOpenChange(false);
          }}
        >
          <Icon name="close" />
        </button>
      </div>
      {description === undefined ? null : (
        <p id={descriptionId} className="muted">
          {description}
        </p>
      )}
      {children}
    </dialog>
  );
}
