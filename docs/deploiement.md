# Mettre ChefOS en ligne

Ce guide s'adresse à quelqu'un qui n'a jamais hébergé de site. À la fin, vous aurez deux adresses en HTTPS :

- le **back** (l'outil du chef), par exemple `https://app.mondomaine.be` ;
- la **vitrine** (le site public du restaurant et ses réservations), par exemple `https://resto.mondomaine.be`.

Tout tourne sur **un seul serveur**, dans des conteneurs Docker : la base PostgreSQL, les deux sites, et **Caddy**, un petit programme qui obtient et renouvelle tout seul les certificats HTTPS (Let's Encrypt). Les fichiers utilisés sont dans le dossier `deploy/` du dépôt.

## 1. Prérequis

| Il vous faut | Remarques |
|---|---|
| **Un serveur (VPS)** sous Linux | 2 Go de mémoire et 20 Go de disque suffisent pour un restaurant. Chez n'importe quel hébergeur (OVH, Hetzner, Scaleway, DigitalOcean…). Notez son **adresse IP publique**. |
| **Docker** avec le plugin **Compose** | Sur Ubuntu ou Debian : `curl -fsSL https://get.docker.com \| sh`. Ensuite, pour lancer `docker` sans `sudo`, ajoutez votre utilisateur au groupe `docker` (`sudo usermod -aG docker $USER`), puis déconnectez-vous et reconnectez-vous en SSH ; sinon, préfixez chaque commande `docker` de ce guide par `sudo`. Vérifiez avec `docker compose version`. |
| **Les ports 80 et 443 ouverts** | Dans le pare-feu du serveur et chez l'hébergeur. Caddy en a besoin pour obtenir les certificats. Rien d'autre ne doit les utiliser. |
| **Un nom de domaine** | Chez n'importe quel bureau d'enregistrement (OVH, Gandi, Namecheap…). Vous pouvez utiliser deux sous-domaines d'un même domaine. |
| **Un compte SMTP** | Pour envoyer les e-mails (confirmations de réservation, mot de passe oublié). Votre fournisseur de messagerie ou un service d'envoi (Brevo, Mailjet, Postmark…) vous donne : serveur, port, identifiant, mot de passe. |
| **Un compte Mollie** | Pour encaisser les acomptes : <https://www.mollie.com/>. Créez une clé API (`test_…` pour essayer, `live_…` pour de vrais paiements). Facultatif pour une simple démonstration. |
| **Une clé API Anthropic** (facultatif) | Pour la lecture automatique des tickets fournisseurs : <https://console.anthropic.com/>. Sans elle, le reste fonctionne. |

Connectez-vous au serveur en SSH. Si `git` n'est pas installé : `sudo apt install -y git`. Puis récupérez le projet :

```bash
git clone <adresse-du-dépôt> chefos
cd chefos/deploy
```

Toutes les commandes `docker compose` de ce guide se lancent depuis ce dossier `deploy/`.

## 2. DNS

Le DNS est l'annuaire qui dit où se trouve chaque nom de domaine. Chez votre bureau d'enregistrement, ajoutez **deux enregistrements de type `A`** qui pointent vers l'adresse IP de votre serveur :

| Nom | Type | Valeur |
|---|---|---|
| `app` (donc `app.mondomaine.be`) | `A` | l'adresse IP du serveur |
| `resto` (donc `resto.mondomaine.be`) | `A` | l'adresse IP du serveur |

N'ajoutez **pas** d'enregistrement `AAAA` (IPv6), même si votre serveur a une adresse IPv6 : le réseau Docker par défaut n'a pas d'IPv6, les visiteurs en IPv6 arriveraient sous l'adresse de la passerelle et partageraient tous la même limite de requêtes, au lieu d'être comptés chacun.

La propagation prend de quelques minutes à quelques heures. Vérifiez depuis votre ordinateur :

```bash
dig +short app.mondomaine.be
dig +short resto.mondomaine.be
```

Les deux commandes doivent afficher l'adresse IP du serveur. **Ne passez pas à la suite avant** : sans cela, Caddy ne peut pas obtenir les certificats.

## 3. Fichier `.env`

Toute la configuration tient dans un fichier `deploy/.env`. Il contient des secrets : il n'est **jamais** versionné (il est dans `.gitignore`). Créez-le à partir du modèle :

```bash
cp .env.example .env
chmod 600 .env
nano .env
```

Les variables, dans l'ordre du fichier :

| Variable | À mettre |
|---|---|
| `BACK_DOMAIN` | Le domaine du back, sans `https://` (ex. `app.mondomaine.be`). |
| `VITRINE_DOMAIN` | Le domaine de la vitrine, sans `https://` (ex. `resto.mondomaine.be`). |
| `ACME_EMAIL` | Votre adresse e-mail : Let's Encrypt s'en sert pour vous prévenir si un certificat ne se renouvelle pas. |
| `POSTGRES_PASSWORD` | Un mot de passe long pour la base. Générez-le avec `openssl rand -hex 24` (lettres et chiffres seulement : évitez `$`, `#` et les espaces). |
| `PAYMENTS_GATEWAY` | `Mollie` pour de vrais paiements (ou de test avec une clé `test_…`), `Demo` pour une démonstration sans argent réel. |
| `MOLLIE_API_KEY` | Votre clé Mollie. Obligatoire si `PAYMENTS_GATEWAY=Mollie`. |
| `EMAIL_SENDER` | `Smtp` pour envoyer de vrais e-mails. `Log` n'écrit rien d'utile dans les journaux (pas le lien de confirmation) : il ne convient qu'à une démonstration avec `DEMO_SEED_ON_STARTUP=true` ; sinon le site refuse de démarrer. |
| `EMAIL_FROM_ADDRESS`, `EMAIL_FROM_NAME` | L'expéditeur affiché (ex. `reservations@mondomaine.be` et `Mon Restaurant`). L'adresse doit être autorisée par votre fournisseur SMTP. |
| `SMTP_HOST`, `SMTP_PORT` | Serveur et port de votre fournisseur (587 en général). |
| `SMTP_SECURITY` | `StartTls` (port 587) ou `SslOnConnect` (port 465). Le mode sans chiffrement est refusé en production. |
| `SMTP_USERNAME`, `SMTP_PASSWORD` | Identifiants SMTP. |
| `ANTHROPIC_API_KEY` | Clé Anthropic pour les tickets. Peut rester vide. |
| `RECEIPTS_READER` | `Claude` (par défaut) ou `Demo` (ticket factice, sans clé). |
| `ACCOUNTS_ALLOW_REGISTRATION` | Laissez `false` (voir l'étape 5). |
| `DEMO_SEED_ON_STARTUP`, `DEMO_EMAIL`, `DEMO_PASSWORD` | Laissez `false` et vide pour un vrai restaurant. Pour une **démonstration** : `true`, plus l'adresse et le mot de passe du compte de démonstration : « Le Petit Bistro » est alors créé au démarrage. |

Les deux sites se refusent à démarrer si la configuration est incomplète (voir « Dépannage »).

## 4. Premier démarrage

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

La première fois, la construction des images prend plusieurs minutes. Le démarrage se fait dans cet ordre : la base, puis le **back** (qui crée ou met à jour le schéma de la base : jusqu'à une ou deux minutes), puis la **vitrine**, qui attend que le back soit prêt.

Suivez l'avancement :

```bash
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f back
```

Quand les quatre services (`db`, `back`, `vitrine`, `caddy`) sont `Up` et que `back` et `vitrine` sont `healthy`, ouvrez `https://app.mondomaine.be` dans votre navigateur. Au premier chargement, Caddy obtient le certificat : comptez quelques secondes.

## 5. Premier compte

**L'inscription est ouverte tant qu'aucun compte n'existe.** Ouvrez `https://app.mondomaine.be`, cliquez sur « Créer un compte » et créez le compte du chef : un e-mail de confirmation arrive, cliquez sur son lien, puis connectez-vous.

Dès qu'un compte existe, **l'inscription se ferme** : personne d'autre ne peut créer un compte en ligne. Pour en ajouter un (un associé, un deuxième restaurant) :

1. mettez `ACCOUNTS_ALLOW_REGISTRATION=true` dans `.env` ;
2. appliquez : `docker compose -f docker-compose.prod.yml up -d` ;
3. faites créer le compte, puis **remettez `false`** et relancez la même commande.

(Si vous avez activé la démonstration, le compte de démonstration existe déjà : l'inscription est donc fermée dès le départ.)

## 6. Sauvegarde et restauration

Toutes les données (restaurants, recettes, réservations, comptes, clés de connexion) sont dans la base PostgreSQL, qui vit dans le volume Docker `chefos_chefos-pg` (le nom du projet Compose, `chefos`, puis le nom du volume, `chefos-pg`). **Sauvegardez-la régulièrement, et copiez la sauvegarde hors du serveur.**

**Sauvegarder :**

```bash
mkdir -p ~/sauvegardes
docker compose -f docker-compose.prod.yml exec -T db pg_dump -U chefos -d chefos --clean --if-exists | gzip > ~/sauvegardes/sauvegarde-$(date +%F).sql.gz
```

Les sauvegardes sont rangées dans `~/sauvegardes/`, hors du dossier du projet : elles contiennent des données personnelles et ne doivent jamais se retrouver dans le dépôt.

Pour automatiser, placez ces commandes (avec le chemin complet de `deploy/`) dans une tâche `cron` quotidienne, et ne gardez que les dernières sauvegardes.

**Restaurer** (sur ce serveur ou sur un nouveau serveur, après un premier démarrage) :

```bash
docker compose -f docker-compose.prod.yml stop back vitrine
gunzip -c ~/sauvegardes/sauvegarde-2026-01-31.sql.gz | docker compose -f docker-compose.prod.yml exec -T db psql -v ON_ERROR_STOP=1 -U chefos -d chefos
docker compose -f docker-compose.prod.yml start back vitrine
```

Le fichier `.env` n'est pas dans la sauvegarde de la base : gardez-en une copie à part, dans un gestionnaire de mots de passe.

## 7. Mise à jour

```bash
cd chefos
git pull
cd deploy
docker compose -f docker-compose.prod.yml up -d --build
```

Le back applique tout seul les migrations de la base au démarrage, puis la vitrine redémarre. Les personnes connectées restent connectées (les clés de session sont en base). Faites une sauvegarde avant une mise à jour importante.

Pour nettoyer les anciennes images : `docker image prune -f`.

## 8. Vérifications après déploiement

1. **Santé des deux sites.** Ces deux adresses doivent afficher `Healthy` :
   - `https://app.mondomaine.be/healthz/ready`
   - `https://resto.mondomaine.be/healthz/ready`

   (`/healthz` seul dit que le site répond ; `/healthz/ready` vérifie en plus que la base est joignable et à jour.)
2. **E-mail de test.** Sur la page de connexion du back, cliquez sur « Mot de passe oublié », saisissez l'adresse de votre compte : l'e-mail doit arriver dans la minute. S'il n'arrive pas, regardez les journaux : `docker compose -f docker-compose.prod.yml logs back | grep -i mail`, et revérifiez `SMTP_*` et `EMAIL_FROM_ADDRESS`.
3. **Webhook Mollie** (si `PAYMENTS_GATEWAY=Mollie`). Mollie prévient la vitrine par l'adresse `https://resto.mondomaine.be/<slug-du-restaurant>/webhooks/mollie` ; elle est fournie automatiquement à chaque paiement, il n'y a rien à saisir. Faites une réservation de test avec une clé `test_…`, payez-la, puis dans le **tableau de bord Mollie**, ouvrez le paiement : l'appel du webhook doit y apparaître avec un statut `200`. Une fois satisfait, remplacez la clé par `live_…`, puis relancez avec `up -d`.
4. **Réservation de bout en bout.** Sur la vitrine, faites une réservation : l'e-mail de confirmation arrive, et son lien permet de consulter et d'annuler.

## 9. Limites

- **Une instance par site.** Cette composition fait tourner un seul exemplaire du back et un seul de la vitrine, sur un seul serveur. Ne lancez pas plusieurs exemplaires, et n'hébergez pas deux sites différents sur la même base sans prévoir de séparation.
- **Pas de haute disponibilité** : pendant une mise à jour ou un redémarrage du serveur, les sites sont indisponibles quelques secondes à quelques minutes.
- **Sauvegardes à votre charge** (étape 6) : rien n'est sauvegardé automatiquement.
- **Clés de connexion non chiffrées au repos** dans la base : protégez l'accès au serveur et aux sauvegardes.
- **Sécurité du réseau.** Seul Caddy est exposé (ports 80 et 443). Ne publiez jamais les ports des sites ni de la base : c'est ce qui permet à Caddy d'indiquer le vrai visiteur aux sites. Le sous-réseau Docker `172.30.0.0/16` est fixé dans la composition ; s'il entre en conflit avec le réseau de votre hébergeur, changez-le **à la fois** dans `networks.chefos` et dans `ReverseProxy__KnownNetworks__0` de `docker-compose.prod.yml`.

## Dépannage

**Le back ou la vitrine redémarre en boucle (`Restarting`, code de sortie 134).** C'est voulu : le site refuse de démarrer plutôt que d'échouer plus tard, et Docker le relance sans fin. Lisez la raison dans les journaux :

```bash
docker compose -f docker-compose.prod.yml logs back
```

Vous y trouvez un message de ce type, avec une ligne par réglage à corriger (exemple : `PAYMENTS_GATEWAY=Mollie` sans `MOLLIE_API_KEY`) :

```text
Unhandled exception. System.InvalidOperationException: Configuration de production incomplète :
- Payments:Gateway = Mollie sans clé : renseignez Mollie:ApiKey ou la variable d'environnement MOLLIE_API_KEY.
```

Les autres lignes possibles : `Vitrine:BaseUrl` qui doit commencer par `https://`, `Email:Sender = Smtp sans serveur` ou sans adresse d'expédition, `Email:Smtp:Security = None` interdit, `Email:FromAddress` qui n'est pas une adresse valide, `Email:Sender = Log` sans démonstration (le lien de confirmation ne partirait jamais). Les messages ne contiennent jamais vos secrets (les lignes qui suivent, `at ChefOS…`, sont des détails techniques : ignorez-les). Corrigez `.env`, puis `docker compose -f docker-compose.prod.yml up -d`.

**`docker compose` répond « required variable … is missing a value ».** Une variable obligatoire de `.env` est vide (`POSTGRES_PASSWORD`, `PAYMENTS_GATEWAY`, `BACK_DOMAIN`, `VITRINE_DOMAIN`, `ACME_EMAIL`) : le message dit laquelle. Remplissez-la. (Un `PAYMENTS_GATEWAY` oublié est arrêté ici, avant même le démarrage du site.)

**L'e-mail de confirmation n'arrive pas.** Lisez les journaux du back (`docker compose -f docker-compose.prod.yml logs back | grep -i mail`) : ils disent si l'envoi a été refusé (adresse d'expédition non autorisée, identifiants SMTP erronés, serveur injoignable). Corrigez les réglages `SMTP_*` et `EMAIL_FROM_ADDRESS` dans `.env`, appliquez avec `docker compose -f docker-compose.prod.yml up -d`, puis, sur la page de connexion, cliquez sur « Renvoyer l'e-mail de confirmation ».

**Le navigateur affiche une erreur de certificat ou « connexion impossible ».** Vérifiez que le DNS pointe bien vers le serveur (étape 2), que les ports 80 et 443 sont ouverts, puis lisez `docker compose -f docker-compose.prod.yml logs caddy`. Let's Encrypt limite le nombre d'essais : corrigez la cause avant de relancer en boucle.

**Le back reste « starting » longtemps.** Au premier démarrage, il crée le schéma de la base avant de répondre : patientez jusqu'à deux minutes, en suivant `logs -f back`.

**Erreur « Bad Request – Invalid Hostname » (code 400) sur un site.** L'adresse saisie ne correspond pas à `BACK_DOMAIN` ou `VITRINE_DOMAIN` : chaque site n'accepte que son propre domaine.
