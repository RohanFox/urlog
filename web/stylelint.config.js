// A colour, a font, a size of type, a space or a radius is written in src/styles/tokens.css and nowhere else.
const tokenOnly = {
  ignoreValues: ['0', 'auto', 'inherit', 'none', 'currentColor', 'transparent', '100%', '/^calc\\(/'],
  ignoreVariables: true,
  ignoreFunctions: false,
  expandShorthand: true,
};

export default {
  plugins: ['stylelint-declaration-strict-value'],
  rules: {
    'scale-unlimited/declaration-strict-value': [
      [
        '/color$/',
        'fill',
        'stroke',
        'background',
        'font-family',
        'font-size',
        'font-weight',
        'line-height',
        'letter-spacing',
        'border-radius',
        'box-shadow',
        'gap',
        'row-gap',
        'column-gap',
        '/^padding/',
        '/^margin/',
      ],
      tokenOnly,
    ],
    'color-no-hex': true,
    'function-disallowed-list': ['rgb', 'rgba', 'hsl', 'hsla'],
    'declaration-property-unit-disallowed-list': { '/.*/': ['px', 'em', 'rem', 'ms', 's'] },
  },
  overrides: [
    {
      files: ['**/tokens.css'],
      rules: {
        'scale-unlimited/declaration-strict-value': null,
        'color-no-hex': null,
        'function-disallowed-list': null,
        'declaration-property-unit-disallowed-list': null,
      },
    },
  ],
};
