
export interface Tokens {
  access_token: string;
  access_expires_at: string;
}

export interface Me {
  id: string;
  email: string;
  display_name: string | null;
  must_change_password: boolean;
  must_enroll_two_factor: boolean;
  /** До какого времени опасные действия не спросят код; null — спросят. */
  step_up_until?: string | null;
}

export interface SecondFactorChallenge {
  challenge: string;
  method: 'totp' | 'email' | string;
  available?: string[];
}

export interface TwoFactorStatus {
  confirmed: boolean;
  available_methods: string[] | null;
}

export interface TotpSetup {
  secret: string;
  uri: string;
}

export interface PasswordPolicy {
  min_length: number;
  require_uppercase: boolean;
  require_lowercase: boolean;
  require_digit: boolean;
  require_symbol: boolean;
}

export interface KeyManagerStatus {
  enabled: boolean;
  configured: boolean;
  api_key_set: boolean;
  last_poll_at: string | null;
  last_poll_error: string | null;
  last_poll_count: number | null;
}

export interface KnownCertificate {
  common_name: string;
  serial_number: string;
  expires_on: string | null;
  first_seen_at: string;
  last_seen_at: string;
}

export interface MailStatus {
  configured: boolean;
  password_set: boolean;
  pending: number;
  failed: number;
  last_error: string | null;
}

export interface Setting {
  key: string;
  title: string;
  category: string;
  type: string;
  is_security: boolean;
  nullable: boolean;
  minimum: number | null;
  maximum: number | null;
  choices: string[] | null;
  value: unknown;
  default: unknown;
}

export interface User {
  id: string;
  email: string;
  display_name: string | null;
  is_active: boolean;
  activated_at: string | null;
  locked_until: string | null;
  authenticator_enabled: boolean;
}

export interface UsersPage {
  items: User[];
  next_cursor: string | null;
}

export interface UserCreated {
  invitation_token: string | null;
}

export interface ResetIssued {
  reset_token: string | null;
}

export interface Session {
  id: string;
  user_agent: string | null;
  address: string | null;
  last_seen_at: string;
  current: boolean;
}

export type ServerStatus =
  | 'disabled' | 'unchecked' | 'error' | 'expired' | 'deploy_failed' | 'name_stale' | 'update_pending' | 'expiring' | 'unbound' | 'ok';

/** Сертификат своего имени на сервере: Kerio подаёт его по SNI. */
export interface ExtraCert {
  common_name: string;
  status: ServerStatus;
  status_text: string;
  source: SourceCert | null;
  installed_serial: string | null;
  installed_not_after: string | null;
  installed_at: string | null;
  kerio_id: string | null;
  /** Подаётся ли по своему имени: null — не проверено. */
  served_by_name: boolean | null;
  last_error: string | null;
  next_attempt_at: string | null;
  added_at: string;
}

export interface Served {
  sha256: string | null;
  subject: string | null;
  issuer: string | null;
  serial: string | null;
  not_before: string | null;
  not_after: string | null;
  days_left: number | null;
}

export interface Installed {
  serial: string | null;
  sha256: string | null;
  not_before: string | null;
  not_after: string | null;
  at: string | null;
  kerio_id: string | null;
}

export interface SourceCert {
  common_name: string;
  serial_number: string;
  expires_on: string | null;
  first_seen_at: string;
}

export interface KerioServer {
  id: string;
  name: string;
  base_url: string;
  username: string;
  password_set: boolean;
  enabled: boolean;
  auto_deploy: boolean;
  cleanup_old: boolean;
  verify_ports: number[];
  notify_emails: string[];
  tls_mode: 'ca' | 'pin';
  tls_server_name: string | null;
  tls_pin_sha256: string | null;
  common_name: string | null;
  status: ServerStatus;
  status_text: string;
  busy: boolean;
  last_check_at: string | null;
  last_check_error: string | null;
  auth_rejected: boolean;
  kerio_version: string | null;
  kerio_role: string | null;
  kerio_certificate_count: number | null;
  served: Served | null;
  installed: Installed | null;
  source: SourceCert | null;
  last_deploy_at: string | null;
  last_deploy_error: string | null;
  next_attempt_at: string | null;
  /** Очередь установки по порядку: первым встанет первый. */
  install_queue: string[];
  /** Что ставится на этом сервере прямо сейчас. */
  installing: string | null;
  created_at: string;
  updated_at: string;
  store: StoreCert[];
  store_checked_at: string | null;
  extras: ExtraCert[];
  /** Что подаётся по SNI: имя → SHA-256. */
  served_by_name: Record<string, string>;
}

/** Откуда сертификат в хранилище Kerio. */
export type Origin = 'managed' | 'kmp' | 'builtin' | 'foreign' | 'request';

export type CertState = 'expired' | 'expiring' | 'ok' | 'unknown';

/** Сертификат в хранилище Kerio. */
export interface StoreCert {
  id: string;
  type: string;
  subject: string;
  issuer: string;
  not_before: string | null;
  not_after: string | null;
  days_left: number | null;
  serial: string | null;
  sha256: string | null;
  dns_names: string[];
  self_signed: boolean;
  untrusted: boolean;
  served: boolean;
  origin: Origin;
  kmp_common_name: string | null;
  state: CertState;
  update_from: string | null;
}

/** Отправленное оповещение. */
export interface SentNotification {
  id: string;
  at: string;
  server_id: string | null;
  server_name: string | null;
  event: string;
  level: 'info' | 'warning' | 'critical';
  title: string;
  recipients: string[];
  queued: number;
}

export type Outcome = 'running' | 'succeeded' | 'failed' | 'rolled_back' | 'dry_run';

export interface DeployStep {
  at: string;
  step: string;
  message: string;
  ok: boolean;
}

export interface Deployment {
  id: string;
  server_id: string;
  server_name: string;
  trigger: string;
  outcome: Outcome;
  started_at: string;
  finished_at: string | null;
  actor_id: string | null;
  common_name: string;
  serial_number: string;
  sha256: string | null;
  not_after: string | null;
  previous_sha256: string | null;
  kerio_certificate_id: string | null;
  previous_kerio_certificate_id: string | null;
  failed_step: string | null;
  error: string | null;
  steps: DeployStep[] | null;
}

export interface Overview {
  servers: KerioServer[];
  source: { certificates: number };
  auto_deploy: boolean;
  dry_run: boolean;
  warning_days: number;
  interval_minutes: number;
}

export interface Probe {
  host: string;
  port: number;
  sha256: string;
  subject: string;
  issuer: string;
  serial: string;
  not_before: string;
  not_after: string;
  dns_names: string[];
  self_signed: boolean;
  chain_trusted: boolean;
  suggested_mode: 'ca' | 'pin';
  suggested_server_name: string | null;
  same_certificate_ports: number[];
  pem: string;
}

export interface KerioCert {
  id: string;
  type: string;
  subject: string;
  issuer: string;
  valid_to: string | null;
  self_signed: boolean;
}

export interface LoginTest {
  ok: boolean;
  error: string | null;
  version: string | null;
  login_name: string | null;
  role: string | null;
  full_admin: boolean;
  certificates: KerioCert[];
  suggested_common_names: string[];
}
