# BTCPayServer GNU Taler Plugin

BTCPay plugin to accept GNU Taler payments with multi-asset support (`CHF`, `KUDOS`).

## Warning
This is experimental software.
GNU Taler is in active development and upstream protocol/API behavior may change.
This plugin can break between BTCPay or Taler upgrades and should be deployed with caution in production.

## Quick production checklist
1. Copy plugin into BTCPay and install the generated `.btcpay` package.
2. Deploy `taler-merchant` + `taler-merchant-db` using the provided BTCPay docker fragment.
3. Ensure merchant template contains `BASE_URL = "${TALER_MERCHANT_BASE_URL}"`.
4. Set `/root/BTCPayServer/.env`: `TALER_MERCHANT_BASE_URL=https://<your-host>/taler-merchant/`.
5. Ensure fragment passes `TALER_MERCHANT_BASE_URL` into `taler-merchant` container environment.
6. Update nginx policies to block public access to taler merchant 
7. In `Server settings -> Taler`: set internal `Merchant base URL` and public `Merchant public base URL`.
8. Initialize instance, generate API token, add bank account, fetch/enable assets, then restart BTCPay.
9. Create a fresh invoice and verify QR/Pay link uses valid `taler://pay/...` URI and payment is detected.

## Add a bank account

Use the following Payto URI format: `payto://iban/CH00000000000000000000?receiver-name=My%20Company%SA`

In order to receive CHF from `taler-ops.ch` you will have to have your iban added and follow the instructions:

If the bank account status is `kyc-wire-required` you will need to send from the same bank account the smallest amount possible to the payto instructions. It might take 1 or 2 days to complete.


## Features
- Server-level Taler settings UI
- Store-level enable/disable per Taler asset
- Auto-discovery of assets from merchant `/config`
- Merchant instance self-provisioning from UI (init instance, token generation, bank account checks/add)
- Invoice checkout integration with Taler QR and wallet link
- Background payment listener to settle BTCPay invoices when merchant reports paid orders

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

## Merchant backend deployment with btcpayserver-docker
Copy files into your btcpayserver-docker checkout:
- `docker-fragments/opt-add-taler-merchant.custom.yml` -> `docker-compose-generator/docker-fragments/opt-add-taler-merchant.custom.yml`
- `docker/taler-merchant/` -> `docker-taler-merchant/`

Enable fragment in `/root/BTCPayServer/.env`:
```bash
BTCPAYGEN_ADDITIONAL_FRAGMENTS=...;opt-add-taler-merchant.custom
```

Set merchant base URL env (used by merchant config template):
```bash
TALER_MERCHANT_BASE_URL=https://<your-host>/taler-merchant/
```

Important:
- The fragment must pass `TALER_MERCHANT_BASE_URL` into `taler-merchant` service environment.
- `docker-taler-merchant/merchant.conf` should include:
```ini
BASE_URL = "${TALER_MERCHANT_BASE_URL}"
```

Regenerate/redeploy:
```bash
. ./btcpay-setup.sh -i
docker compose -f Generated/docker-compose.generated.yml up -d --build --force-recreate taler-merchant taler-merchant-db
```

Verify loaded value:
```bash
docker exec -it generated-taler-merchant-1 taler-merchant-config -s merchant -o BASE_URL -f
```

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

## Common issues
- `instance not found` (`code: 2000`): initialize instance first.
- `no active bank account` (`code: 2500`): add bank account to the instance.
- `legal limits` (`code: 2513`): exchange/KYC constraint, not a BTCPay plugin bug.
- Asset list empty until restart: restart BTCPay after changing enabled assets.

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
