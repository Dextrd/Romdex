# EmulatorJS, self-hosted: the player Romdex's web app loads games into. It's GPL-3.0, so it's a container of its own,
# and only ever loaded by the browser, never linked into the core. The EmulatorJS player plugin says which core plays
# what, and the core passes its files on at /players/{id}/.
FROM alpine:3.22 AS fetch
ARG EMULATORJS_VERSION=4.2.3
ARG EMULATORJS_SHA256=07d451bc06fa3ad04ab30d9b94eb63ac34ad0babee52d60357b002bde8f3850b
RUN apk add --no-cache 7zip curl
RUN curl -fsSL -o /emulatorjs.7z "https://github.com/EmulatorJS/EmulatorJS/releases/download/v${EMULATORJS_VERSION}/${EMULATORJS_VERSION}.7z" \
 && echo "${EMULATORJS_SHA256}  /emulatorjs.7z" | sha256sum -c - \
 && mkdir /emulatorjs && 7z x -o/emulatorjs /emulatorjs.7z > /dev/null \
 && rm /emulatorjs.7z && find /emulatorjs -maxdepth 2 | head -40

FROM nginxinc/nginx-unprivileged:1.29-alpine
COPY --from=fetch /emulatorjs /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080
