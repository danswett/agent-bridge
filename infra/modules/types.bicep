@export()
@description('One relay app in a stamp.')
type appConfig = {
  @description('Container App name; its FQDN derives from it, so production keeps the live name.')
  name: string
  @description('Full image reference. infra/deploy.ps1 passes what is running now, so a stamp deploy never rolls an image; relay.yml does that.')
  image: string
  @description('Build the image came from (the commit SHA), reported by /healthz.')
  version: string
  identityName: string
  @description('Globally unique, 3-24 characters.')
  keyVaultName: string
  minReplicas: int
  @description('1 until relay homes/forwarding (design §5.8) exist: presence is in memory, so a second replica would split accounts.')
  @maxValue(1)
  maxReplicas: int
  @description('vCPU as a string, for example \'0.25\'.')
  cpu: string
  @description('Memory, for example \'0.5Gi\' (twice the vCPU on consumption).')
  memory: string
  @description('The vault holds github-client-secret: the relay then revokes each GitHub token as soon as sign-in has read the account id.')
  githubClientSecret: bool?
  purgeProtection: bool?
  @description('Keep a system-assigned identity alongside the app\'s own (production, which was created with one).')
  keepSystemIdentity: bool?
}
