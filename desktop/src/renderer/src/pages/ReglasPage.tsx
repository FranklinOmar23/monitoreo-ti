import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { RuleItem, RuleOverride } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../data/PanelData';

/** "> 80 %", "< 20 %", "inmediato". */
function threshold(rule: Pick<RuleItem, 'unit' | 'lessIsWorse' | 'hasThresholds'>, value: number | null): string {
  if (!rule.hasThresholds) return value === null ? '—' : 'Inmediato';
  if (value === null) return '—';
  return `${rule.lessIsWorse ? '<' : '>'} ${value} ${rule.unit}`.trim();
}

const duration = (rule: RuleItem, minutes: number) => (rule.hasDuration ? `${minutes} min` : '—');

/** W-22 Reglas de alerta (empresa y sucursales) y W-23 edición. */
export function ReglasPage({ canManage }: { canManage: boolean }) {
  const api = usePanelApi();
  const rules = useQuery({ queryKey: ['reglas'], queryFn: () => api.rules() });
  const [editing, setEditing] = useState<RuleItem | null>(null);

  if (rules.isPending) return <Loading />;
  if (rules.isError) return <ErrorState error={rules.error} onRetry={() => void rules.refetch()} />;
  if (rules.data.length === 0) return <EmptyState title="Sin reglas" />;

  return (
    <>
      <div className="table-card">
        <table className="data-table">
          <thead>
            <tr>
              <th>Regla</th>
              <th>Advertencia</th>
              <th>Crítico</th>
              <th>Duración</th>
              <th>Estado</th>
              <th>Ajustes por sucursal</th>
              {canManage && <th aria-label="Acciones" />}
            </tr>
          </thead>
          <tbody>
            {rules.data.map((rule) => (
              <tr key={rule.metric} className={rule.available ? undefined : 'is-muted'}>
                <td>
                  <strong className="rule-name">{rule.name}</strong>
                  <span className="row-sub">
                    {rule.description}
                    {!rule.available && ' · Se evaluará cuando esté su módulo (impresoras o red).'}
                  </span>
                </td>
                <td className="mono">{rule.hasThresholds ? threshold(rule, rule.warning) : '—'}</td>
                <td className="mono">{threshold(rule, rule.critical)}</td>
                <td className="mono">{duration(rule, rule.durationMinutes)}</td>
                <td>
                  <span className="alert-status" data-status={rule.isActive ? 'activa' : 'inactiva'}>
                    {rule.isActive ? 'Activa' : 'Inactiva'}
                  </span>
                  {rule.id === null && <span className="row-sub">Valor por defecto</span>}
                </td>
                <td>{rule.overrides.length === 0 ? '—' : rule.overrides.map((o) => o.branchName).join(', ')}</td>
                {canManage && (
                  <td>
                    <button type="button" className="btn btn-outline" onClick={() => setEditing(rule)}>
                      Ajustar
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted note">
        Cada sucursal usa su propio ajuste si lo tiene; si no, la regla de la empresa. Los cambios se aplican en la
        próxima revisión (cada minuto).
      </p>
      {editing && (
        <RuleForm
          rule={rules.data.find((r) => r.metric === editing.metric) ?? editing}
          onClose={() => setEditing(null)}
        />
      )}
    </>
  );
}

function RuleForm({ rule, onClose }: { rule: RuleItem; onClose: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const [scope, setScope] = useState('');

  const current: Pick<RuleOverride, 'warning' | 'critical' | 'durationMinutes' | 'isActive'> =
    rule.overrides.find((o) => String(o.branchId) === scope) ?? rule;
  const override = rule.overrides.find((o) => String(o.branchId) === scope);

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['reglas'] });
  };

  const remove = useMutation({
    mutationFn: (id: number) => api.deleteRule(id),
    onSuccess: refresh,
  });

  return (
    <Modal title={`Ajustar: ${rule.name}`} onClose={onClose} wide>
      <div className="form-grid form-grid-single">
        <label className="field">
          <span>Aplicar a</span>
          <select value={scope} onChange={(e) => setScope(e.target.value)}>
            <option value="">Toda la empresa</option>
            {(branches.data ?? []).map((b) => (
              <option key={b.id} value={b.id}>
                Sucursal {b.name}
                {rule.overrides.some((o) => o.branchId === b.id) ? ' (con ajuste)' : ''}
              </option>
            ))}
          </select>
        </label>
        {/* La clave reinicia el formulario al cambiar de alcance. */}
        <RuleValues
          key={scope}
          rule={rule}
          branchId={scope ? Number(scope) : null}
          initial={current}
          onSaved={() => {
            refresh();
            onClose();
          }}
        />
        {remove.isError && (
          <p className="auth-error" role="alert">
            {remove.error.message}
          </p>
        )}
        {(override || (!scope && rule.id !== null)) && (
          <button
            type="button"
            className="link-quiet rule-reset"
            disabled={remove.isPending}
            onClick={() => remove.mutate(override ? override.id : rule.id!)}
          >
            {override
              ? 'Quitar el ajuste de esta sucursal (usará la regla de la empresa)'
              : 'Volver a los valores por defecto'}
          </button>
        )}
      </div>
    </Modal>
  );
}

function RuleValues({
  rule,
  branchId,
  initial,
  onSaved,
}: {
  rule: RuleItem;
  branchId: number | null;
  initial: Pick<RuleOverride, 'warning' | 'critical' | 'durationMinutes' | 'isActive'>;
  onSaved: () => void;
}) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    warning: initial.warning === null ? '' : String(initial.warning),
    critical: initial.critical === null ? '' : String(initial.critical),
    duration: String(initial.durationMinutes),
    active: initial.isActive,
  });

  const save = useMutation({
    mutationFn: () =>
      api.saveRule({
        metrica: rule.metric,
        sucursalId: branchId,
        umbralAdvertencia: form.warning === '' ? null : Number(form.warning),
        umbralCritico: form.critical === '' ? null : Number(form.critical),
        duracionMin: Number(form.duration) || 0,
        activa: form.active,
      }),
    onSuccess: onSaved,
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  const unit = rule.unit ? ` (${rule.unit})` : '';
  const sign = rule.lessIsWorse ? 'menor que' : 'mayor que';

  return (
    <form className="form-grid" onSubmit={submit} aria-label="Valores de la regla">
      {save.isError && (
        <p className="auth-error form-span" role="alert">
          {save.error.message}
        </p>
      )}
      {rule.hasThresholds ? (
        <>
          <label className="field">
            <span>
              Advertencia si es {sign}
              {unit}
            </span>
            <input
              type="number"
              min={0}
              step="any"
              value={form.warning}
              onChange={(e) => setForm({ ...form, warning: e.target.value })}
            />
          </label>
          <label className="field">
            <span>
              Crítico si es {sign}
              {unit}
            </span>
            <input
              type="number"
              min={0}
              step="any"
              value={form.critical}
              onChange={(e) => setForm({ ...form, critical: e.target.value })}
            />
          </label>
        </>
      ) : (
        <p className="muted form-span">Esta regla no tiene umbrales: alerta como crítica en cuanto ocurre.</p>
      )}
      {rule.hasDuration && (
        <label className="field">
          <span>Sostenido durante (min)</span>
          <input
            type="number"
            min={0}
            max={1440}
            value={form.duration}
            onChange={(e) => setForm({ ...form, duration: e.target.value })}
          />
        </label>
      )}
      <label className="check form-span">
        <input type="checkbox" checked={form.active} onChange={(e) => setForm({ ...form, active: e.target.checked })} />
        Regla activa
      </label>
      <div className="form-span rule-actions">
        <button type="submit" className="btn btn-primary" disabled={save.isPending}>
          {save.isPending ? 'Guardando…' : 'Guardar'}
        </button>
      </div>
    </form>
  );
}
