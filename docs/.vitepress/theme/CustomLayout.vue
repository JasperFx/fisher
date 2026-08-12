<script setup>
import DefaultTheme from 'vitepress/theme'

const { Layout } = DefaultTheme
</script>

<template>
  <Layout>
    <template #aside-ads-before>
      <div class="info-box">
        <p>The search box knows all the secrets -- try it!</p>
      </div>
      <div class="info-box">
        <p>Fisher is part of the <a class="vp-external-link-icon" href="https://jasperfx.net" target="_blank" rel="noreferrer">Critter Stack</a> ecosystem.</p>
      </div>
      <div class="info-box">
        <p>
          <img src="https://jasperfx.net/logo.png" alt="JasperFx Logo" />
          JasperFx provides formal support for Fisher and other Critter Stack libraries. Please check our <a class="vp-external-link-icon" href="https://jasperfx.net/support-plans/" target="_blank" rel="noreferrer">Support Plans</a> for more details.
        </p>
      </div>
    </template>
  </Layout>
</template>

<style scoped>
    .info-box {
        border-top: 1px solid var(--vp-c-divider); padding: 3px;
        font-size: 0.85em;
    }

    .info-box a {
        color: var(--vp-c-brand-1);
    }
</style>
