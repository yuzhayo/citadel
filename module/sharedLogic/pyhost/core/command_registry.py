"""Command registry generic untuk pyhost core.

Registrasi command dipisah dari protokol NDJSON: table statis HANDLERS
pyhost.py di-render dari registry, dan plugin (mis. fitur Add Profile
milik camoprof) mendaftarkan namespace-nya sendiri lewat
``register(plugin)`` tanpa menyentuh file core ini.

Core tidak tahu nama command apapun di luar yang didaftarkan padanya —
tidak ada semantik Google, enrollment, Add Profile, atau fitur lain
di sini. Arah dependensi selalu plugin -> core, tidak pernah sebaliknya.
"""

from providers import PyhostError, log


class CommandRegistry:
    """Peta command-name -> handler ``async (host, msg) -> dict``.

    Garis-garis besar kontrak:
    - nama command harus namespaced (``<owner>.<domain>.<verb>``) —
      kolisi namespace dua pemilik = konfigurasi rusak, ditolak keras;
    - handler menerima ``host`` (pemilik lifecycle) dan ``msg`` mentah;
    - satu nama hanya punya satu handler (pendaftaran ulang nama yang
      sama = error developer, bukan override diam-diam).
    """

    def __init__(self):
        self._handlers = {}
        self._namespaces = {}

    def register_namespace(self, owner, prefix, handlers):
        """Daftarkan sekumpulan handler di bawah ``owner``.

        ``prefix`` contoh: ``camoprof`` untuk command
        ``camoprof.add_profile.start``. Semua nama harus diawali
        ``prefix.`` — mendaftarkan nama di luar namespace sendiri
        ditolak (owner lain mungkin memilikinya).
        """
        if not prefix or "." in prefix:
            raise ValueError("prefix namespace tidak sah: %r" % (prefix,))
        existing = self._namespaces.get(prefix)
        if existing is not None and existing != owner:
            raise PyhostError(
                "NAMESPACE_COLLISION",
                "prefix %r sudah dimiliki %r" % (prefix, existing))
        self._namespaces[prefix] = owner
        for name, handler in handlers.items():
            # Command top-level (satu segmen: "ping", "shutdown") milik
            # namespace tanpa titik; command bertingkat harus diawali
            # prefix owner — mencegah mendaftar nama di luar wilayahnya.
            parts = name.split(".")
            if len(parts) > 1 and not name.startswith(prefix + "."):
                raise ValueError(
                    "command %r bukan bagian dari prefix %r" % (name, prefix))
            if name in self._handlers:
                raise PyhostError(
                    "COMMAND_COLLISION",
                    "command sudah terdaftar: %r" % (name,))
            self._handlers[name] = handler
            log("command terdaftar: %s (%s)" % (name, owner))

    def get(self, name):
        handler = self._handlers.get(name)
        if handler is None:
            return None
        return handler
