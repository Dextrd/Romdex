# The igir worker: igir, chdman and DolphinTool behind a small HTTP API, the only thing that writes the library. These
# tools are GPL, so they live here, apart from the MIT core. Debian ships both disc tools, but inside packages that
# bring a desktop's worth of other things, so each is taken out here with just the libraries it needs.
FROM docker.io/library/node:24-trixie-slim AS tools
RUN apt-get update \
    && apt-get install -y --no-install-recommends dolphin-emu mame-tools \
    && mkdir -p /out/bin /out/lib \
    && cp /usr/games/dolphin-tool /usr/bin/chdman /out/bin/ \
    && ldd /out/bin/* | awk '/=> \// { print $3 }' | sort -u | xargs -I{} cp -L {} /out/lib/

FROM docker.io/library/node:24-trixie-slim
LABEL org.opencontainers.image.title="Romdex igir worker" \
      org.opencontainers.image.description="Files games into a Romdex library with igir, and converts them with chdman and DolphinTool." \
      org.opencontainers.image.source="https://github.com/dextrd/Romdex" \
      org.opencontainers.image.licenses="MIT AND GPL-3.0-or-later AND GPL-2.0-or-later"
COPY --from=tools /out/bin/ /usr/local/bin/
# Only the libraries this image doesn't already have.
RUN --mount=type=bind,from=tools,source=/out/lib,target=/tool-libs \
    for lib in /tool-libs/*; do ldconfig -p | grep -q "/$(basename "$lib")\$" || cp "$lib" /usr/local/lib/; done \
    && ldconfig \
    && ! ldd /usr/local/bin/dolphin-tool /usr/local/bin/chdman | grep "not found"

WORKDIR /worker
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund && npm cache clean --force \
    && mkdir -p /staging /roms && chmod 1777 /staging
COPY src src
COPY test test

ENV STAGING_ROOT=/staging LIBRARY_ROOT=/roms PORT=8080 NODE_ENV=production
VOLUME ["/staging", "/roms"]
EXPOSE 8080
# Run as the user that owns the game library, so files it writes belong to them.
USER 1000:1000
CMD ["node", "src/server.js"]
