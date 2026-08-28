# Build context is typescript/ (not this directory): the sample consumes @vsaga/* through npm
# workspaces, so the packages it depends on have to be inside the context to be linked.
FROM node:22-alpine AS build
WORKDIR /src

COPY package.json package-lock.json tsconfig.base.json tsconfig.json ./
COPY packages ./packages
COPY samples ./samples

RUN npm ci
RUN npm run build --workspace @vsaga/protocol \
    && npm run build --workspace @vsaga/participant \
    && npm run build --workspace @vsaga/transport-rabbitmq \
    && npm run build --workspace @vsaga/sample-notification-participant

# --omit=dev after the build, so the runtime image carries amqplib but none of the toolchain.
RUN npm prune --omit=dev

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production

COPY --from=build /src/node_modules ./node_modules
COPY --from=build /src/packages ./packages
COPY --from=build /src/samples ./samples

USER node
CMD ["node", "samples/notification-participant/dist/index.js"]
