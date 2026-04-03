FROM debian:trixie-slim

ARG DEBIAN_FRONTEND=noninteractive
ARG DEBIAN_CODENAME=trixie-testing
ARG TALER_MERCHANT_VERSION=v1.5.12

RUN apt-get update \
  && apt-get install -y --no-install-recommends ca-certificates curl gnupg gettext-base \
  && mkdir -p /etc/apt/keyrings \
  && curl -fsSL https://taler.net/taler-systems.gpg -o /etc/apt/keyrings/taler-systems.gpg \
  && echo "deb [signed-by=/etc/apt/keyrings/taler-systems.gpg] https://deb.taler.net/apt/debian ${DEBIAN_CODENAME} main" > /etc/apt/sources.list.d/taler.list \
  && apt-get update \
  && apt-get install -y --no-install-recommends taler-merchant=${TALER_MERCHANT_VERSION#v}* postgresql-client \
  && rm -rf /var/lib/apt/lists/*

COPY merchant.conf /etc/taler-merchant/merchant.conf.template
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 9966

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
