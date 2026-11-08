# Étape 4 — décisions prises et points à reprendre

L'utilisateur a validé le parcours client (maquette) et l'approche de la bibliothèque de vins, puis a délégué le reste (« je te fais confiance »). Les choix restants sont ceux de Claude ; ils sont marqués *(Claude)* dans `specs/2026-11-07-vitrine-reservation-design.md`.

## Décisions prises en cours de route

- **Places.**
  - Verrou consultatif PostgreSQL par restaurant, service et jour local (clé SHA-256 de « service|date »). Il est pris à la création en ligne, à la création manuelle et à la confirmation d'un paiement tardif.
  - Un service qui a des réservations à venir ne peut être ni renommé ni supprimé.
- **Argent.**
  - Le montant est toujours recalculé côté serveur. Le formulaire envoie le montant affiché, et toute différence est refusée.
  - Un paiement reçu après expiration est remboursé si la place a été reprise ; reçu après une annulation du client, il est remboursé.
  - Remboursements idempotents : clé `refund-{id passerelle}`, seul le reliquat est remboursé, avec réconciliation si l'enregistrement a échoué après un remboursement réussi.
  - L'annulation et la demande de remboursement sont enregistrées dans la même transaction.
  - La commande en cuisine indique le montant déjà réglé en ligne.
- **Règle d'annulation.** La date limite d'annulation gratuite est figée sur chaque réservation (`FreeCancellationUntilUtc`) au moment où elle est prise.
- **Paiements.**
  - Mode Demo persisté en base, partagé entre le back et la vitrine.
  - Mollie en REST v2.
  - Webhook par restaurant (`/{slug}/webhooks/mollie`) qui renvoie 503 en cas d'échec pour que Mollie réessaie.
  - Le back peut revérifier un paiement resté « en attente » (« Vérifier le paiement »).
  - Un paiement passé par une autre passerelle est refusé avec un message clair.
- **Vins.**
  - Bibliothèque partagée de 71 vins.
  - Une fiche rédigée par l'IA ne peut être publiée qu'après la case « J'ai relu la fiche », vérifiée côté serveur.
- **Site public.**
  - Restaurant résolu par son slug, et seulement si la vitrine est publiée (404 sinon, partout).
  - Couleur d'accent contrastée (AA).
  - `noindex` sur la page de réservation et la page de paiement démo.
- **Arrivée en salle.** La commande en cuisine est créée à partir des plats pré-commandés aux prix figés, avec le nom de chaque convive en note.

## À reprendre

- **À corriger rapidement.** `VitrineService.cs:350` lit le fuseau horaire sans filtrer sur le restaurant courant. Le contrôle « service avec réservations à venir » peut donc utiliser le fuseau d'un autre restaurant (correction d'une ligne).
- **Vérification automatique des paiements dans le back.**
  - Elle n'est pas bornée dans le temps : prévoir un délai global de 3 à 5 s et traiter d'abord les paiements les plus récents.
  - Ajouter un index sur (`RestaurantId`, `GatewayPaymentId`).
- **Dialogue d'annulation du back.** La case « rembourser » est cochée par défaut selon la règle actuelle, et non selon la date limite figée.
- **Concurrence.** Le contrôle de renommage d'un service n'est pas verrouillé contre une réservation simultanée. Prendre le verrou du service.
- **Production.**
  - Configurer `ForwardedHeaders` derrière un proxy, fixer explicitement `Payments:Gateway`, et exposer le webhook en HTTPS public.
  - Le mode Demo journalise seulement un avertissement hors développement.
  - Démarrer le back avant la vitrine sur une base vide, pour éviter une course sur la bibliothèque de vins.
- **Taille des fichiers.** `ReservationService` (environ 660 lignes), `Reserve.razor`, `VitrineSettings.razor` et `ReservationDay.razor` sont à découper.
- **Divers.**
  - Les réservations manuelles doivent tomber sur la grille des créneaux.
  - Ajouter une fermeture exceptionnelle ne prévient pas s'il existe déjà des réservations ce jour-là.
  - Les e-mails ne sont pas envoyés réellement : ils sont seulement journalisés.
