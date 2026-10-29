import type { ReactNode } from 'react';

interface CardProps {
  title?: string;
  /** Contenido a la derecha del título (leyenda, enlace, selector). */
  aside?: ReactNode;
  className?: string;
  children: ReactNode;
}

export function Card({ title, aside, className, children }: CardProps) {
  return (
    <section className={`card${className ? ` ${className}` : ''}`}>
      {(title || aside) && (
        <header className="card-header">
          {title && <h2>{title}</h2>}
          {aside}
        </header>
      )}
      {children}
    </section>
  );
}
