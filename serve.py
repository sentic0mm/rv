# Kleiner Webserver für index.html. Gestartet über "Server starten.bat".
# Liefert absichtlich nur die eine Seite aus, nicht den restlichen Ordner.
# Liegt cloudflared.exe daneben, wird zusätzlich ein Internet-Link erzeugt (Name zufällig).
import http.server, os, re, socket, subprocess, sys, threading

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, 'index.html')
PORTS = range(8080, 8091)
CLOUDFLARED = os.path.join(HERE, 'cloudflared.exe')


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.split('?')[0] not in ('/', '/index.html', '/repo_valuables.html'):
            self.send_error(404)
            return
        data = open(PAGE, 'rb').read()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_HEAD(self):
        self.do_GET()

    def log_message(self, fmt, *args):
        if self.path in ('/', '/index.html', '/repo_valuables.html'):
            print('  Aufruf von', self.client_address[0])


class Server(http.server.ThreadingHTTPServer):
    # Unter Windows erlaubt SO_REUSEADDR zwei Server auf demselben Port; dann würde der
    # zweite Start nie auf den nächsten Port ausweichen
    allow_reuse_address = False

    def server_bind(self):
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def local_ips():
    ips = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.add(info[4][0])
    except OSError:
        pass
    # Adresse der Standardroute, falls getaddrinfo sie nicht liefert
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(('8.8.8.8', 80))
        ips.add(s.getsockname()[0])
        s.close()
    except OSError:
        pass
    # 192.168.56.x ist der VirtualBox-Host-Adapter, von außen nicht erreichbar
    return sorted(ip for ip in ips if not ip.startswith(('127.', '169.254.', '192.168.56.')))


def label(ip):
    if ip.startswith('26.'):
        return 'Radmin VPN'
    return 'Heimnetz / WLAN'


def tunnel(port):
    proc = subprocess.Popen([CLOUDFLARED, 'tunnel', '--no-autoupdate', '--url', f'http://localhost:{port}'],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors='replace')
    for line in proc.stdout:
        m = re.search(r'https://[\w-]+\.trycloudflare\.com', line)
        if m:
            print(f'\n  Internet (für alle):   {m.group(0)}\n')
            break
    for _ in proc.stdout:  # weiterlesen, sonst blockiert cloudflared bei vollem Puffer
        pass


def main():
    if not os.path.exists(PAGE):
        sys.exit('index.html fehlt - erst "python build.py" ausführen.')
    for port in PORTS:
        try:
            server = Server(('0.0.0.0', port), Handler)
            break
        except OSError:
            continue
    else:
        sys.exit(f'Kein freier Port zwischen {PORTS.start} und {PORTS.stop - 1}.')

    print('\n  R.E.P.O. Valuables läuft!\n')
    print(f'  Dieser PC:             http://localhost:{port}')
    for ip in local_ips():
        print(f'  {label(ip) + ":":<22} http://{ip}:{port}')
    if os.path.exists(CLOUDFLARED):
        print('\n  Internet-Link wird erstellt ...')
        threading.Thread(target=tunnel, args=(port,), daemon=True).start()
    else:
        print('\n  (Nur im eigenen Netz / Radmin erreichbar. Für einen Internet-Link')
        print('   cloudflared.exe in diesen Ordner legen.)')
    print('\n  Fenster schließen oder Strg+C beendet den Server.\n')
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == '__main__':
    main()
