import { ApiError } from '../../api/client';
import type { JobControl, JobSnapshot, VoxCpmGenerationInfo } from '../../api/types';
import { terminal } from './useProjectEvents';

const labels: Record<JobSnapshot['state'], string> = { queued: 'В очереди', preparing: 'Подготовка', running: 'Озвучивание', pause_requested: 'Завершаем фрагмент перед паузой', paused: 'На паузе', cancel_requested: 'Останавливаем задание', canceled: 'Отменено', interrupted: 'Прервано — можно продолжить', failed: 'Ошибка задания', completed: 'Завершено' };
export function JobProgress({ job, pending, control, reuse, reuseDisabled }: { job: JobSnapshot; pending: boolean; control: (job: JobSnapshot, command: JobControl) => Promise<void>; reuse?: (info: VoxCpmGenerationInfo) => void; reuseDisabled?: boolean }) {
  const pause = ['queued', 'preparing', 'running'].includes(job.state);
  const resume = ['paused', 'interrupted'].includes(job.state);
  const captured = job.voxCpmGeneration;
  const results = job.audioResults?.slice(0, 32) ?? [];
  return <article className="job-progress" aria-label={job.kind === 'preview' ? 'Пробное задание' : 'Задание озвучивания'}>
    <div className="section-heading"><h3>{job.kind === 'preview' ? 'Проба голоса' : 'Озвучивание фрагментов'}</h3><span className="state-badge">{labels[job.state]}</span></div>
    <p role="status">Готово: {job.completedUnits} из {job.totalUnits}</p>
    {job.totalUnits > 0 && <progress aria-label="Прогресс готовых фрагментов" value={job.completedUnits} max={job.totalUnits} />}
    {job.state === 'running' && <p className="subtle">Создаётся текущий фрагмент. Время зависит от компьютера; точный срок пока неизвестен.</p>}
    {job.state === 'queued' && ['voxcpm2-saved-adapter-cpu-v1', 'voxcpm2-saved-adapter-reference-cpu-v1', 'voxcpm2-saved-adapter-controls-cpu-v1'].includes(job.effectiveProfileId) && job.diagnosticCode === 'worker.runtime.not_ready'
      ? <p className="notice warning">Движок выбранной модели VoxCPM2 сейчас недоступен. Проверьте его настройку или отмените задание. Студия продолжит его, когда движок снова станет готов; другой голос не подставляется.</p>
      : job.diagnosticCode && <p className="notice error">{new ApiError(job.diagnosticCode).message}</p>}
    {!terminal(job) && <div className="generation-actions">
      {pause && <button className="quiet" disabled={pending} onClick={() => void control(job, 'pause')}>Пауза</button>}
      {resume && <button disabled={pending} onClick={() => void control(job, 'resume')}>Продолжить задание</button>}
      <button className="quiet" disabled={pending || job.state === 'cancel_requested'} onClick={() => void control(job, 'cancel')}>Отменить задание</button>
    </div>}
    {captured && <div className="captured-settings"><p>VoxCPM2 · модель <code>{captured.modelId}</code> · шаг {captured.globalStep}</p>
      <p>Seed: {captured.options.seed} · шаги синтеза: {captured.options.timesteps} · CFG: {captured.options.cfg} · темп WAV: {captured.options.tempoPercent}%</p>
      <p>{captured.options.useReference ? <>Опорный клип: {captured.referenceTrainingOrdinal == null ? '—' : captured.referenceTrainingOrdinal + 1} · {captured.referenceSeconds?.toFixed(1) ?? '—'} с · <code>{captured.referenceClipId}</code> · {captured.options.referenceClipId === null ? 'автоматический выбор' : 'выбран вручную'}</> : 'Опорный клип отключён; обученная модель применена.'}</p>
      {reuse && <button type="button" className="quiet" disabled={reuseDisabled} onClick={() => reuse(captured)}>Использовать эти настройки</button>}
    </div>}
    {results.length > 0 && <ol className="segment-list">{results.map((result, index) => {
      const url = `/api/projects/${encodeURIComponent(job.projectId)}/artifacts/${encodeURIComponent(result.artifactId)}`;
      return <li key={result.artifactId}><p>Фрагмент {result.order + 1} · {result.seconds.toFixed(1)} с</p><p>{result.spokenText}</p>
        <audio aria-label={`Аудио результата ${index + 1}`} controls preload="none" src={url} /><a href={url} download={`fragment-${result.order + 1}.wav`}>Скачать WAV · фрагмент {result.order + 1}</a></li>;
    })}</ol>}
    {job.completedUnits > 32 && <p className="subtle">Показаны первые 32 сохранённых результата из {job.completedUnits}.</p>}
  </article>;
}
