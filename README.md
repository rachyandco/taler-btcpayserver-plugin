# BTCPayServer GNU Taler Plugin

BTCPay plugin to accept GNU Taler payments with multi-asset support (`CHF`, `KUDOS`).

## Warning
This is experimental software.
GNU Taler is in active development and upstream protocol/API behavior may change.
This plugin can break between BTCPay or Taler upgrades and should be deployed with caution in production.

## Quick production checklist
1. Copy nginx vhost rules in `/var/lib/docker/volumes/generated_nginx_vhost/_data/<your-host>`
2. Copy `docker-fragments/opt-add-taler-merchant.custom.yml` in `docker-compose-generator/docker-fragments/opt-add-taler-merchant.custom.yml`
3. Run `export BTCPAYGEN_ADDITIONAL_FRAGMENTS="$BTCPAYGEN_ADDITIONAL_FRAGMENTS;opt-add-taler-merchant.custom"`
4. Run `export TALER_MERCHANT_BASE_URL=https://<your-host>/taler-merchant/`
5. Run `. ./btcpay-setup.sh -i`
6. In `Server settings -> Taler`: set public `Merchant public base URL` to `https://<your-host>/taler-merchant/`
7. Initialize instance, generate API token, then `Save` then restart BTCPay.
8. Fetch/enable assets and add a bank account
9. Follow the wire and KYC instructions to enable the bank account


## Add a bank account

Use the following Payto URI format: `payto://iban/CH00000000000000000000?receiver-name=My%20Company%SA`

Warning: only CHF iban are currently supported by the Taler exchange.

In order to receive CHF from `taler-ops.ch` you will have to have your iban added and follow the instructions:

If the bank account status is `kyc-wire-required` you will need to send from the same bank account the smallest amount possible to the payto instructions. It might take 1 or 2 days to complete.

Once the bank account is on status `kyc-required` you will be requested to validate the Terms of Services of the Taler exchange.

## Repository layout
- Plugin code: `BTCPayServer.Plugins.Taler/`
- Standalone merchant docker: `docker-compose.taler.yml`
- Merchant image/context for BTCPay docker: `docker/taler-merchant/`
- BTCPay docker fragment: `docker-fragments/opt-add-taler-merchant.custom.yml`

## Build plugin
Prereqs:
- .NET 8 SDK
- BTCPay source available at `submodules/btcpayserver`

Build:
```bash
dotnet publish BTCPayServer.Plugins.Taler/BTCPayServer.Plugins.Taler.csproj -c Release -o /tmp/taler-plugin-publish --no-restore -m:1
```

The output directory contains the plugin payload used to create a `.btcpay` package for upload.

## BTCPay server settings
Go to `Server settings -> Taler` and configure:
- `Merchant base URL`: internal URL reachable by BTCPay container, typically `http://taler-merchant:9966/`
- `Merchant public base URL`: public URL used in checkout links, typically `https://<your-host>/taler-merchant/`
- `Merchant instance ID`: usually `default`
- `Instance password`
- `Merchant API token`

Then:
- `Initialize instance`
- `Generate API token` (uses `scope: all` and `duration: forever`)
- `Check bank accounts`
- `Fetch assets`
- Save and restart BTCPay when asset set changes

## Nginx reverse proxy (public merchant path)
Expose merchant public endpoints through BTCPay nginx at `/taler-merchant/` over HTTPS.

Minimal vhost snippet:
```nginx
location /taler-merchant/ {
    proxy_pass http://taler-merchant:9966/;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto https;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}

location ~ ^/taler-merchant/(private|webui|management|instances/[^/]+/private)/ {
    return 403;
}
```

add this in `/var/lib/docker/volumes/generated_nginx_vhost/_data/<your-host>`


## API/token notes
- Merchant private API calls use `Authorization: Bearer secret-token:...`
- Token scope must allow required operations. `all` is used for provisioning flows.
- If you see `401` on private endpoints, regenerate token and save it in BTCPay.

## Merchant API endpoints (used in this project)
There is no runtime "list all endpoints" endpoint in `taler-merchant`.  
For backend `20:0:8`, these are the relevant endpoints this plugin/deployment uses:

- Public:
  - `GET /config`
  - `GET /instances/{instance}/orders/{order_id}?token={claim_token}`
- Provisioning/management:
  - `POST /management/instances`
- Instance private (Bearer token):
  - `POST /instances/{instance}/private/token`
  - `GET /instances/{instance}/private/accounts`
  - `POST /instances/{instance}/private/accounts`
  - `DELETE /instances/{instance}/private/accounts/{h_wire}`
  - `GET /instances/{instance}/private/kyc`
  - `GET /instances/{instance}/private/orders`
  - `POST /instances/{instance}/private/orders`
  - `GET /instances/{instance}/private/orders/{order_id}`

Canonical upstream reference:
- https://docs.taler.net/core/api-merchant.html


## Some CLI commands

Run the following commands inside BTCPayServer, replace `secret-token:yoursecret` with the `Merchant API token`

- Read Merchant Backend config (public endpoint)
```
docker run --rm --network generated_default curlimages/curl:8.12.1 -sS  http://taler-merchant:9966/config
```

- Read all accounts in Merchant Backend
```
docker run --rm --network generated_default curlimages/curl:8.12.1 -i -sS \
    -H "Authorization: Bearer secret-token:yoursecret" \
    "http://taler-merchant:9966/instances/default/private/accounts"
```

- check KYC status of all accounts
```
docker run --rm --network generated_default curlimages/curl:8.12.1 -sS \
    -H "Authorization: Bearer secret-token:yoursecret" \
    "http://taler-merchant:9966/instances/default/private/kyc"
```

- list all orders
```
docker run --rm --network generated_default curlimages/curl:8.12.1 -sS \
    -H "Authorization: Bearer secret-token:yoursecret" \
    "http://taler-merchant:9966/instances/default/private/orders"
```

- list all paid and wired orders
```
docker run --rm --network generated_default curlimages/curl:8.12.1 -sS \
    -H "Authorization: Bearer secret-token:yoursecret" \
    "http://taler-merchant:9966/instances/default/private/orders?paid=yes&wired=yes&delta=-50"
```


## License
GPLv3. See `LICENSE`.