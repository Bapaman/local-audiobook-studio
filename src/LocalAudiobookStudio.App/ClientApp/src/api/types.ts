export interface Bootstrap { version: string; antiforgeryRequestToken: string; capabilities: { generation: boolean; training: boolean } }
export interface ProjectListItem { id: string; displayName: string; createdUtc: string; lastOpenedUtc: string; revision: number; hasRecording: boolean; hasAnchor: boolean; hasText: boolean }
export interface VoiceRecording { artifactId: string; sha256: string; byteLength: number; durationMilliseconds: number; hasClippingWarning: boolean }
export interface VoiceAnchor { artifactId: string; startMilliseconds: number; endMilliseconds: number; transcript: string }
export interface TextDocument { sourceKind: string; displayName: string; encoding: string; sha256: string; byteCount: number; originalText: string; normalizedText: string }
export interface Segment { id: string; order: number; originalText: string; normalizedText: string; spokenText: string; contentHash: string; status: string; outputArtifactId?: string | null }
export interface ProjectSnapshot { id: string; displayName: string; revision: number; voiceRecording: VoiceRecording | null; voiceAnchor: VoiceAnchor | null; textDocument: TextDocument | null; segments: Segment[]; hasAcceptedText?: boolean; hasAcceptedVoice?: boolean }
export interface SnapshotResponse { snapshot: ProjectSnapshot; etag: string }
export interface ConfirmAnchor { startMilliseconds: number; endMilliseconds: number; transcript: string; pairListenedAndConfirmed: boolean }
export type JobKind = 'preview' | 'generation';
export type JobState = 'queued' | 'preparing' | 'running' | 'pause_requested' | 'paused' | 'cancel_requested' | 'canceled' | 'interrupted' | 'failed' | 'completed';
export type JobControl = 'pause' | 'resume' | 'cancel';
export type SavedVoiceSelection =
  | { savedModelId: string; savedVoxCpmModelId?: never; experimentalConfirmed: boolean; voxCpmOptions?: never }
  | { savedVoxCpmModelId: string; savedModelId?: never; experimentalConfirmed: boolean; voxCpmOptions?: VoxCpmGenerationOptions };
export interface VoxCpmGenerationOptions { seed: number; timesteps: number; cfg: number; tempoPercent: number; useReference: boolean; referenceClipId: string | null }
export interface VoxCpmReferenceClip { id: string; trainingOrdinal: number; seconds: number; text: string }
export interface VoxCpmReferenceCatalog { modelId: string; corpusVersionId: string; defaultClipId: string | null; clips: VoxCpmReferenceClip[] }
export interface VoxCpmGenerationInfo { modelId: string; globalStep: number; options: VoxCpmGenerationOptions; referenceClipId: string | null; referenceTrainingOrdinal: number | null; referenceSeconds: number | null }
export interface JobAudioResult { segmentId: string; order: number; artifactId: string; seconds: number; spokenText: string }
export interface JobSnapshot { id: string; projectId: string; kind: JobKind; state: JobState; requestedState: 'run' | 'pause' | 'cancel'; phase: string; completedUnits: number; totalUnits: number; revision: number; createdUtc: string; updatedUtc: string; completedUtc: string | null; diagnosticCode: string | null; inputHash: string; effectiveProfileId: string; voxCpmGeneration?: VoxCpmGenerationInfo | null; audioResults?: JobAudioResult[] | null }
export interface JobResponse { snapshot: JobSnapshot; etag: string }
export interface RuntimeReadiness { ready: boolean; experimental: boolean; diagnosticCode: string | null; profileId: string }
export type TrainingState = 'queued' | 'preparing' | 'training' | 'stop_requested' | 'paused' | 'completed' | 'interrupted' | 'failed';
export type TrainingControl = 'stop' | 'resume';
export interface TrainingReadiness { ready: boolean; diagnosticCode: string | null; experimental: true; cpuThreads: number }
export interface TrainedModelVersion { id: string; globalStep: number; completedEpoch: number; createdUtc: string; qualityEstablished: false; selectable?: boolean }
export interface TrainingSnapshot { id: string; projectId: string; revision: number; state: TrainingState; phase: string; sourceRecordingId: string; globalStep: number; completedEpoch: number; epochSize: number; createdUtc: string; updatedUtc: string; diagnosticCode: string | null; canResume: boolean; models: TrainedModelVersion[]; executionId?: string | null }
export interface TrainingResponse { snapshot: TrainingSnapshot; etag: string }
export type TtsCorpusState = 'queued' | 'preparing' | 'transcribing' | 'stop_requested' | 'paused' | 'completed' | 'interrupted' | 'failed';
export interface TtsCorpusReadiness { ready: boolean; diagnosticCode: string | null; provider: string; cpuThreads: number }
export interface TtsCorpusWord { text: string; start: number; end: number | null }
export interface TtsCorpusClip { id: string; index: number; startSample: number; endSample: number; text: string; excluded: boolean; reviewed: boolean; warningCodes: string[]; words: TtsCorpusWord[] }
export interface TtsCorpusVersion { id: string; sha256: string; clipCount: number; retainedDurationSeconds: number; createdUtc: string }
export interface TtsCorpusSnapshot { id: string; projectId: string; revision: number; state: TtsCorpusState; phase: string; sourceRecordingId: string; sourceDurationSeconds: number; retainedDurationSeconds: number; totalClipCount: number | null; completedClipCount: number; clips: TtsCorpusClip[]; versions: TtsCorpusVersion[]; canResume: boolean; diagnosticCode: string | null; createdUtc: string; updatedUtc: string; executionId?: string | null }
export interface TtsCorpusResponse { snapshot: TtsCorpusSnapshot; etag: string }
export interface TtsCorpusClipUpdate { text: string; excluded: boolean; reviewed: boolean }
export type VoxCpmTrainingState = 'queued' | 'loading' | 'training' | 'checkpointing' | 'stop_requested' | 'paused' | 'completed' | 'interrupted' | 'failed';
export interface VoxCpmTrainingReadiness { ready: boolean; diagnosticCode: string | null; provider: string; cpuThreads: number }
export interface VoxCpmTrainingModel { id: string; trainingId: string; corpusVersionId: string; manifestSha256: string; globalStep: number; completedEpochs: number; createdUtc: string }
export interface VoxCpmTrainingSnapshot { id: string; projectId: string; revision: number; state: VoxCpmTrainingState; phase: string;
  corpusVersionId: string; corpusManifestSha256: string; corpusClipCount: number; retainedDurationSeconds: number; globalStep: number; completedEpochs: number;
  nextRetainedOrdinal: number; nextClipId: string; targetCompletedEpochs: number; executionId: string; models: VoxCpmTrainingModel[]; canResume: boolean;
  qualityEstablished: false; diagnosticCode: string | null; createdUtc: string; updatedUtc: string }
export interface VoxCpmTrainingResponse { snapshot: VoxCpmTrainingSnapshot; etag: string }
