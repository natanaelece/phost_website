/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './wwwroot/**/*.html',
    './wwwroot/**/*.js',
    '!./wwwroot/admin/assets/vendor/**/*.js',
    '!./wwwroot/admin/assets/build/**/*.js',
    '!./wwwroot/assets/build/**/*.js'
  ],
  theme: {
    extend: {}
  },
  plugins: []
};
