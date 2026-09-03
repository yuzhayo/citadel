"""Kontrak protokol milik fitur Add Profile (camoprof).

Berbeda dari sharedLogic (infrastruktur generik), file ini milik fitur
dan boleh menyebut Google/password. Disalin sebagai package
``camoprof_add_profile`` ke samping pyhost.py saat deploy; pyhost core
memuatnya sebagai plugin dan mendaftarkan namespace
``camoprof.add_profile.*``.

Error codes fitur:
  HEADLESS_ENROLLMENT   session bukan headed
  ENROLLMENT_ACTIVE     profile sudah punya enrollment aktif
  ENROLLMENT_NOT_FOUND  tidak ada enrollment untuk session itu
  ENROLLMENT_NOT_COMPLETE  finish sebelum state complete
  ENROLLMENT_CONSUMED   finish kedua kali; secret sudah diserahkan
  WRONG_ACCOUNT         identitas aktif beda dari expected_email
  SESSION_BUSY          session dimiliki owner lain (dari core)
"""

STATE_ARMED = "armed"
STATE_PASSWORD_OBSERVED = "password_observed"
STATE_WAITING = "waiting_for_google"
STATE_CHALLENGE = "challenge"
STATE_COMPLETE = "complete"
STATE_CONSUMED = "consumed"
STATE_CANCELLED = "cancelled"
STATE_EXPIRED = "expired"
STATE_BROWSER_GONE = "browser_gone"
STATE_WRONG_ACCOUNT = "wrong_account"
STATE_FAILED = "failed"

CAPTURING_STATES = frozenset(
    (STATE_ARMED, STATE_PASSWORD_OBSERVED, STATE_WAITING, STATE_CHALLENGE))
TERMINAL_STATES = frozenset(
    (STATE_CONSUMED, STATE_CANCELLED, STATE_EXPIRED, STATE_BROWSER_GONE,
     STATE_WRONG_ACCOUNT, STATE_FAILED))

ENROLLMENT_TIMEOUT_SEC = 600.0
MAX_PASSWORD_LENGTH = 1024
