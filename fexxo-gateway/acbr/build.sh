#!/usr/bin/env bash
set -euo pipefail
bash /acbr/patches.sh
cd /acbr
find fortesreport-ce Pacotes/Lazarus -name "*.lpk" -print0 | while IFS= read -r -d '' pacote; do
  lazbuild --lazarusdir=/opt/lazarus --add-package-link "$pacote" >/dev/null 2>&1 || true
done
cd Projetos/ACBrLib/Fontes/NFSe
lazbuild --lazarusdir=/opt/lazarus --build-mode=Linux-x86_64 --widgetset=nogui ACBrLibNFSe.lpi >/tmp/acbr-build.log 2>&1 || { tail -40 /tmp/acbr-build.log; exit 1; }
test -f bin/Linux/ST/libacbrnfse64.so
